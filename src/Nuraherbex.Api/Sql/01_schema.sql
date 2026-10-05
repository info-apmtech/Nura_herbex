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
GO


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
GO


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
GO


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
GO


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
GO


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
GO


CREATE TABLE [site_settings] (
    [Key] nvarchar(100) NOT NULL,
    [Value] nvarchar(max) NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_site_settings] PRIMARY KEY ([Key])
);
GO


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
GO


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
GO


CREATE INDEX [IX_customers_Email] ON [customers] ([Email]);
GO


CREATE INDEX [IX_customers_Phone] ON [customers] ([Phone]);
GO


CREATE INDEX [IX_orders_CreatedAt] ON [orders] ([CreatedAt]);
GO


CREATE INDEX [IX_orders_CustomerPhone] ON [orders] ([CustomerPhone]);
GO


CREATE INDEX [IX_orders_ShiprocketAwb] ON [orders] ([ShiprocketAwb]);
GO


CREATE INDEX [IX_payments_GatewayPaymentId] ON [payments] ([GatewayPaymentId]);
GO


CREATE INDEX [IX_payments_OrderId] ON [payments] ([OrderId]);
GO


CREATE UNIQUE INDEX [IX_products_Sku] ON [products] ([Sku]);
GO


CREATE UNIQUE INDEX [IX_trust_batches_BatchNo] ON [trust_batches] ([BatchNo]);
GO


CREATE INDEX [IX_whatsapp_messages_CreatedAt] ON [whatsapp_messages] ([CreatedAt]);
GO


CREATE INDEX [IX_whatsapp_messages_WaMessageId] ON [whatsapp_messages] ([WaMessageId]);
GO


