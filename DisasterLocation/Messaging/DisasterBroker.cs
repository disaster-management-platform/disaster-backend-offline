using RabbitMQ.Client;

namespace DisasterLocation.Messaging;

internal static class DisasterBroker
{
    internal const string Commands = "disasters.commands";
    internal const string Events = "disasters.events";
    internal const string Queue = "disasters.create";
    internal static Task<IConnection> ConnectAsync(IConfiguration config, CancellationToken ct) => new ConnectionFactory {
        HostName = config["Ingestion:HostName"] ?? "localhost", Port = config.GetValue<int>("Ingestion:Port", 5672),
        VirtualHost = config["Ingestion:VirtualHost"] ?? "/", UserName = config["Ingestion:UserName"] ?? "guest",
        Password = config["Ingestion:Password"] ?? "guest", AutomaticRecoveryEnabled = false,
        ConsumerDispatchConcurrency = (ushort)config.GetValue<int>("Ingestion:Consumers", 2)
    }.CreateConnectionAsync(ct);
    internal static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Commands, ExchangeType.Direct, true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(Events, ExchangeType.Topic, true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(Commands + ".dlx", ExchangeType.Direct, true, cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue + ".dlq", true, false, false, cancellationToken: ct);
        await channel.QueueBindAsync(Queue + ".dlq", Commands + ".dlx", "create", cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, true, false, false, new Dictionary<string, object?> {
            ["x-dead-letter-exchange"] = Commands + ".dlx", ["x-dead-letter-routing-key"] = "create" }, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Commands, "create", cancellationToken: ct);
        await channel.QueueDeclareAsync(Events + ".pending", true, false, false, cancellationToken: ct);
        await channel.QueueBindAsync(Events + ".pending", Events, "#", cancellationToken: ct);
    }
}
