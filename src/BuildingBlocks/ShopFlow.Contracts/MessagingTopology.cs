namespace ShopFlow.Contracts;

/// <summary>
/// Messaging entity names. The same values are in <c>infra/modules/messaging/messaging-topology.json</c>,
/// from which Bicep creates the entities (<c>loadJsonContent</c>). Tests in
/// <c>tests/ShopFlow.Contracts.Tests</c> (names, filters, sessions) and <c>tests/ShopFlow.Architecture.Tests</c>
/// (routes) check that both agree.
/// </summary>
/// <remarks>
/// AZURE: entities are created by Bicep only. Applications have no role that allows managing the
/// namespace (only Data Sender / Data Receiver on specific entities), so a typo in a name ends with
/// a MessagingEntityNotFound error at startup instead of silently creating a new queue nobody watches.
/// </remarks>
public static class MessagingTopology
{
    public static class Queues
    {
        public const string InventoryCommands = "inventory-commands";
        public const string PaymentsCommands = "payments-commands";
        public const string ShippingCommands = "shipping-commands";
        public const string SagaReplies = "saga-replies";
        public const string NotificationsInbox = "notifications-inbox";
        public const string ReadModelsInbox = "readmodels-inbox";
        public const string AnalyticsInbox = "analytics-inbox";
        public const string InvoiceBlobEvents = "invoice-blob-events";
    }

    public static class Topics
    {
        public const string OrdersEvents = "orders-events";
        public const string InventoryEvents = "inventory-events";
        public const string PaymentsEvents = "payments-events";
        public const string ShippingEvents = "shipping-events";
    }

    public static class Subscriptions
    {
        /// <summary>Read directly by order-saga; starts the orchestration.</summary>
        public const string SagaStart = "saga-start";

        /// <summary>On every topic: participant replies, forwarded to <see cref="Queues.SagaReplies"/>.</summary>
        public const string Saga = "saga";

        public const string Notifications = "notifications";
        public const string ReadModels = "readmodels";
        public const string Analytics = "analytics";

        /// <summary>On payments-events: Payments listens to its own PaymentCaptured event.</summary>
        public const string Invoicing = "invoicing";

        /// <summary>On shipping-events: Orders updates the order status.</summary>
        public const string Orders = "orders";

        /// <summary>On orders-events: OrderConfirmed forwarded to inventory-commands (reservation commit).</summary>
        public const string Inventory = "inventory";
    }

    public static class EventHubs
    {
        public const string Clickstream = "clickstream";
        public const string BusinessEvents = "business-events";

        public static class ConsumerGroups
        {
            public const string FunnelAggregator = "cg-funnel-aggregator";
            public const string BusinessMetrics = "cg-business-metrics";
        }
    }

    /// <summary>
    /// Consumer names. They are part of the inbox key: the same message can be processed
    /// successfully exactly once by each of them.
    /// </summary>
    public static class Consumers
    {
        public const string OrdersProcessors = "orders-processors";
        public const string OrderSaga = "order-saga";
        public const string InventoryWorker = "inventory-worker";
        public const string Payments = "payments-processor";
        public const string Shipping = "shipping-processor";
        public const string Notifications = "notifications-processor";
        public const string ReadModels = "readmodels-projections";
        public const string Analytics = "analytics-processor";
    }

    /// <summary>Values of the <see cref="MessageHeaders.Source"/> property.</summary>
    public static class Sources
    {
        public const string Orders = "orders";
        public const string OrderSaga = "order-saga";
        public const string Inventory = "inventory";
        public const string Payments = "payments";
        public const string Shipping = "shipping";
    }
}
