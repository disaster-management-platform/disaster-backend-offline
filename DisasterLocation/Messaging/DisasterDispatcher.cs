using System.Text;
using System.Text.Json;
using Platform.DAL.Repositories.Disasters;
using RabbitMQ.Client;

namespace DisasterLocation.Messaging;

public sealed class DisasterDispatcher(IServiceScopeFactory scopes, IConfiguration config, ILogger<DisasterDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await DisasterBroker.ConnectAsync(config, ct);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), ct);
                await DisasterBroker.DeclareAsync(channel, ct);
                while (channel.IsOpen && !ct.IsCancellationRequested)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<DisasterCommandStore>();
                    foreach (var item in await store.ClaimEventsAsync(config.GetValue<int>("Ingestion:DispatchBatchSize", 10), ct))
                    {
                        if (await store.RenewEventAsync(item.Id, item.LeaseId, ct) == 0) continue;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        var body = JsonSerializer.SerializeToUtf8Bytes(new { item.Id, Type = item.EventType, item.DisasterId, item.Version, item.OccurredAt, Payload = JsonSerializer.Deserialize<JsonElement>(item.Payload) });
                        await channel.BasicPublishAsync(DisasterBroker.Events, item.EventType, true,
                            new BasicProperties { MessageId = item.Id.ToString(), ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent }, body, timeout.Token);
                        await store.ConfirmEventAsync(item.Id, item.LeaseId, ct);
                    }
                    foreach (var command in await store.ClaimCommandsAsync(config.GetValue<int>("Ingestion:DispatchBatchSize", 10), ct))
                    {
                        if (await store.RenewCommandAsync(command.ClientRequestId, command.LeaseId, ct) == 0) continue;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await channel.BasicPublishAsync(DisasterBroker.Commands, "create", true,
                            new BasicProperties { MessageId = command.ClientRequestId.ToString(), DeliveryMode = DeliveryModes.Persistent },
                            Encoding.UTF8.GetBytes(command.ClientRequestId.ToString()), timeout.Token);
                        await store.ConfirmCommandAsync(command.ClientRequestId, command.LeaseId, ct);
                    }
                    await Task.Delay(config.GetValue<int>("Ingestion:PublishIntervalMilliseconds", 500), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Disaster publication unconfirmed: {ErrorType}", error.GetType().Name); }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
