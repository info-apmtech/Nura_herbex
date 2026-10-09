using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;

namespace Nuraherbex.Api.Services;

// Serializes shipment mutations across API instances. The dedicated SQL session owns
// the lock; no database transaction is held open during a courier HTTP request.
public sealed class OrderOperationLock(NuraDbContext db)
{
    private static readonly SemaphoreSlim[] Local = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1)).ToArray();
    public async Task<IAsyncDisposable> AcquireAsync(string id)
    {
        if (!db.Database.IsSqlServer())
        {
            var gate = Local[(uint)StringComparer.Ordinal.GetHashCode(id) % Local.Length];
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Order is being updated. Please retry.");
            return new Lease(() => { gate.Release(); return ValueTask.CompletedTask; });
        }
        var connection = new SqlConnection(db.Database.GetConnectionString());
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource=@key, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=5000; SELECT @result;";
            command.Parameters.AddWithValue("@key", "nuraherbex-order-" + id);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) < 0) throw new InvalidOperationException("Order is being updated. Please retry.");
            return new Lease(async () =>
            {
                try
                {
                    using var release = connection.CreateCommand();
                    release.CommandText = "EXEC sp_releaseapplock @Resource=@key, @LockOwner='Session';";
                    release.Parameters.AddWithValue("@key", "nuraherbex-order-" + id);
                    await release.ExecuteNonQueryAsync();
                }
                finally { await connection.DisposeAsync(); }
            });
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private sealed class Lease(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }
}