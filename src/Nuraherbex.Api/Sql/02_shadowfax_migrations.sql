IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [coupons] (
        [Code] nvarchar(64) NOT NULL,
        [DiscountType] nvarchar(max) NOT NULL,
        [DiscountValue] decimal(18,2) NOT NULL,
        [MinOrderAmount] decimal(18,2) NOT NULL,
        [MaxDiscountCap] decimal(18,2) NOT NULL,
        [IsActive] bit NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_coupons] PRIMARY KEY ([Code])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [customers] (
        [Id] nvarchar(32) NOT NULL,
        [FullName] nvarchar(max) NOT NULL,
        [Email] nvarchar(256) NOT NULL,
        [Phone] nvarchar(32) NOT NULL,
        [PasswordHash] nvarchar(max) NOT NULL,
        [ShippingAddress] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_customers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [orders] (
        [Id] nvarchar(32) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [CustomerName] nvarchar(200) NOT NULL,
        [CustomerEmail] nvarchar(256) NOT NULL,
        [CustomerPhone] nvarchar(32) NOT NULL,
        [CustomerId] nvarchar(32) NULL,
        [ShippingAddress] nvarchar(max) NOT NULL,
        [Items] nvarchar(max) NOT NULL,
        [Subtotal] decimal(18,2) NOT NULL,
        [DiscountAmount] decimal(18,2) NOT NULL,
        [ShippingFee] decimal(18,2) NOT NULL,
        [TaxAmount] decimal(18,2) NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [CouponCode] nvarchar(max) NULL,
        [PaymentMethod] nvarchar(16) NOT NULL,
        [PaymentStatus] nvarchar(32) NOT NULL,
        [PaymentRef] nvarchar(max) NULL,
        [PayuTxnId] nvarchar(max) NULL,
        [FulfillmentStatus] nvarchar(32) NOT NULL,
        [ShiprocketOrderId] nvarchar(max) NULL,
        [ShiprocketShipmentId] nvarchar(max) NULL,
        [ShiprocketAwb] nvarchar(64) NULL,
        [ShiprocketCourier] nvarchar(max) NULL,
        [ShippingLabelUrl] nvarchar(max) NULL,
        [DeliveryStatus] nvarchar(max) NOT NULL,
        [DeliveryTrackingEvents] nvarchar(max) NOT NULL,
        [Notes] nvarchar(max) NULL,
        CONSTRAINT [PK_orders] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [payments] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] nvarchar(32) NOT NULL,
        [Provider] nvarchar(max) NOT NULL,
        [GatewayOrderId] nvarchar(max) NULL,
        [GatewayPaymentId] nvarchar(450) NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] nvarchar(max) NOT NULL,
        [Status] nvarchar(max) NOT NULL,
        [GatewayResponse] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_payments] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [products] (
        [Id] nvarchar(64) NOT NULL,
        [Sku] nvarchar(64) NOT NULL,
        [Name] nvarchar(300) NOT NULL,
        [Subtitle] nvarchar(max) NULL,
        [Description] nvarchar(max) NULL,
        [Price] decimal(18,2) NOT NULL,
        [CompareAtPrice] decimal(18,2) NULL,
        [CostPrice] decimal(18,2) NOT NULL,
        [StockQuantity] int NOT NULL,
        [WeightKg] decimal(9,3) NOT NULL,
        [LengthCm] decimal(9,2) NOT NULL,
        [BreadthCm] decimal(9,2) NOT NULL,
        [HeightCm] decimal(9,2) NOT NULL,
        [HsnCode] nvarchar(max) NOT NULL,
        [FssaiLicense] nvarchar(max) NULL,
        [Category] nvarchar(max) NULL,
        [ImageUrl] nvarchar(max) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_products] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [reviews] (
        [Id] nvarchar(32) NOT NULL,
        [Name] nvarchar(max) NOT NULL,
        [City] nvarchar(max) NULL,
        [Rating] int NOT NULL,
        [Title] nvarchar(max) NULL,
        [Body] nvarchar(max) NOT NULL,
        [Verified] bit NOT NULL,
        [Approved] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_reviews] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [site_settings] (
        [Key] nvarchar(100) NOT NULL,
        [Value] nvarchar(max) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_site_settings] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [trust_batches] (
        [Id] nvarchar(64) NOT NULL,
        [BatchNo] nvarchar(64) NOT NULL,
        [ProductName] nvarchar(max) NOT NULL,
        [PackSize] nvarchar(max) NOT NULL,
        [MfgDate] nvarchar(max) NOT NULL,
        [ExpDate] nvarchar(max) NOT NULL,
        [Status] nvarchar(max) NOT NULL,
        [DocumentName] nvarchar(max) NULL,
        [DocumentUrl] nvarchar(max) NULL,
        [DocumentType] nvarchar(max) NULL,
        [FileSize] nvarchar(max) NULL,
        [LabName] nvarchar(max) NULL,
        [Notes] nvarchar(max) NULL,
        [QualityChecks] nvarchar(max) NOT NULL,
        [Documents] nvarchar(max) NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_trust_batches] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE TABLE [whatsapp_messages] (
        [Id] bigint NOT NULL IDENTITY,
        [Direction] nvarchar(max) NOT NULL,
        [WaMessageId] nvarchar(200) NOT NULL,
        [Phone] nvarchar(32) NOT NULL,
        [ProfileName] nvarchar(max) NULL,
        [Type] nvarchar(max) NOT NULL,
        [Body] nvarchar(max) NULL,
        [Status] nvarchar(max) NOT NULL,
        [OrderId] nvarchar(max) NULL,
        [RawPayload] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_whatsapp_messages] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_customers_Email] ON [customers] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_customers_Phone] ON [customers] ([Phone]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_orders_CreatedAt] ON [orders] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_orders_CustomerPhone] ON [orders] ([CustomerPhone]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_orders_ShiprocketAwb] ON [orders] ([ShiprocketAwb]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_payments_GatewayPaymentId] ON [payments] ([GatewayPaymentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_payments_OrderId] ON [payments] ([OrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_products_Sku] ON [products] ([Sku]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_trust_batches_BatchNo] ON [trust_batches] ([BatchNo]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_whatsapp_messages_CreatedAt] ON [whatsapp_messages] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_whatsapp_messages_WaMessageId] ON [whatsapp_messages] ([WaMessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005073351_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005073351_InitialCreate', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005100207_AddPaymentAttempts'
)
BEGIN
    CREATE TABLE [payment_attempts] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] nvarchar(32) NOT NULL,
        [TxnId] nvarchar(128) NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [FailureReason] nvarchar(500) NULL,
        [GatewayStatus] nvarchar(64) NULL,
        [PayuPaymentId] nvarchar(64) NULL,
        [PaymentMode] nvarchar(32) NULL,
        [CustomerName] nvarchar(200) NOT NULL,
        [CustomerEmail] nvarchar(256) NOT NULL,
        [CustomerPhone] nvarchar(32) NOT NULL,
        [OrderSnapshotJson] nvarchar(max) NOT NULL,
        [GatewayResponse] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_payment_attempts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005100207_AddPaymentAttempts'
)
BEGIN
    CREATE INDEX [IX_payment_attempts_CreatedAt] ON [payment_attempts] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005100207_AddPaymentAttempts'
)
BEGIN
    CREATE INDEX [IX_payment_attempts_OrderId] ON [payment_attempts] ([OrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005100207_AddPaymentAttempts'
)
BEGIN
    CREATE INDEX [IX_payment_attempts_TxnId] ON [payment_attempts] ([TxnId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005100207_AddPaymentAttempts'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005100207_AddPaymentAttempts', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007040005_AddOrderCourier'
)
BEGIN
    ALTER TABLE [orders] ADD [Courier] nvarchar(16) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007040005_AddOrderCourier'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007040005_AddOrderCourier', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009035517_AddCustomerProfileImage'
)
BEGIN
    ALTER TABLE [customers] ADD [ProfileImageData] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009035517_AddCustomerProfileImage'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009035517_AddCustomerProfileImage', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045700_AddReviewCustomerLink'
)
BEGIN
    ALTER TABLE [reviews] ADD [CustomerId] nvarchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045700_AddReviewCustomerLink'
)
BEGIN
    CREATE INDEX [IX_reviews_CustomerId] ON [reviews] ([CustomerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045700_AddReviewCustomerLink'
)
BEGIN
    ALTER TABLE [reviews] ADD CONSTRAINT [FK_reviews_customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [customers] ([Id]) ON DELETE SET NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045700_AddReviewCustomerLink'
)
BEGIN
    ;WITH UniqueMatches AS
    (
        SELECT r.[Id], MIN(c.[Id]) AS [CustomerId]
        FROM [reviews] AS r
        INNER JOIN [customers] AS c
            ON LOWER(LTRIM(RTRIM(c.[FullName]))) = LOWER(LTRIM(RTRIM(r.[Name])))
        WHERE NULLIF(LTRIM(RTRIM(r.[Name])), N'') IS NOT NULL
        GROUP BY r.[Id]
        HAVING COUNT_BIG(*) = 1
    )
    UPDATE r
    SET [CustomerId] = m.[CustomerId]
    FROM [reviews] AS r
    INNER JOIN UniqueMatches AS m ON m.[Id] = r.[Id]
    WHERE r.[CustomerId] IS NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009045700_AddReviewCustomerLink'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009045700_AddReviewCustomerLink', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [DeliveredAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [FulfillmentReadyAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [LastShippingEventAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [LastTrackingCheckedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [ParcelBreadthCm] decimal(9,2) NOT NULL DEFAULT 0.0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [ParcelHeightCm] decimal(9,2) NOT NULL DEFAULT 0.0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [ParcelLengthCm] decimal(9,2) NOT NULL DEFAULT 0.0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [ParcelWeightKg] decimal(9,3) NOT NULL DEFAULT 0.0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [PickedUpAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [ShipmentBookedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    ALTER TABLE [orders] ADD [ShipmentClientOrderId] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_orders_Courier_ShiprocketAwb] ON [orders] ([Courier], [ShiprocketAwb]) WHERE [Courier] = ''Shadowfax'' AND [ShiprocketAwb] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_orders_ShipmentClientOrderId] ON [orders] ([ShipmentClientOrderId]) WHERE [ShipmentClientOrderId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009103123_AddShadowfaxFulfillment'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009103123_AddShadowfaxFulfillment', N'10.0.12');
END;

COMMIT;
GO

