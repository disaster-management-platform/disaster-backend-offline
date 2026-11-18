using System.Text;
using System.Text.Json;
using DisasterLocation.BLL;
using DisasterLocation.Core;
using Platform.DAL.Repositories.Disasters;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DisasterLocation.Messaging;

public sealed class DisasterConsumers(IServiceScopeFactory scopes, IConfiguration config, ILogger<DisasterConsumers> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var channels = new List<IChannel>();
            try
            {
                await using var connection = await DisasterBroker.ConnectAsync(config, ct);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                for (var i = 0; i < config.GetValue<int>("Ingestion:Consumers", 2); i++)
                {
                    var channel = await connection.CreateChannelAsync(cancellationToken: ct); channels.Add(channel);
                    channel.ChannelShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                    await DisasterBroker.DeclareAsync(channel, ct);
                    await channel.BasicQosAsync(0, 1, false, ct);
                    var consumer = new AsyncEventingBasicConsumer(channel);
                    consumer.ReceivedAsync += async (_, delivery) => {
                        try { await ProcessAsync(channel, delivery, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                        catch (Exception error) {
                            logger.LogWarning("Command remains unacknowledged: {ErrorType}", error.GetType().Name);
                            if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct);
                        }
                    };
                    await channel.BasicConsumeAsync(DisasterBroker.Queue, false, consumer, ct);
                }
                await disconnected.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Disaster consumer disconnected: {ErrorType}", error.GetType().Name); }
            finally { foreach (var channel in channels) await channel.DisposeAsync(); }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        if (!Guid.TryParse(Encoding.UTF8.GetString(delivery.Body.Span), out var id)) {
            await channel.BasicNackAsync(delivery.DeliveryTag, false, false, ct); return;
        }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<DisasterCommandStore>();
            var receipt = await store.GetAsync(id, ct);
            if (receipt is null) { await channel.BasicNackAsync(delivery.DeliveryTag, false, false, ct); return; }
            if (receipt.ResultJson is not null) {
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct); return;
            }
            BatchDisasterResult result;
            try {
                var command = JsonSerializer.Deserialize<CreateDisasterRequest>(receipt.CommandJson)!;
                var saved = await scope.ServiceProvider.GetRequiredService<DisasterService>().CreateAsync(command, id.ToString(), ct);
                result = new(id.ToString(), saved.Created ? BatchDisasterStatus.Created : BatchDisasterStatus.AlreadyProcessed, saved.Disaster);
            }
            catch (DisasterValidationException error) { result = new(id.ToString(), BatchDisasterStatus.ValidationError, Errors: error.Errors); }
            catch (DisasterConflictException) { result = new(id.ToString(), BatchDisasterStatus.Conflict); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) {
                if (attempt < 3) { await Task.Delay(500 * attempt, ct); continue; }
                result = new(id.ToString(), BatchDisasterStatus.RetryableFailure);
            }
            await store.FinishAsync(id, JsonSerializer.Serialize(result, AsyncBatchAcceptance.Json), receipt.RecoveryCount, config.GetValue<int>("Ingestion:RecoveryDelaySeconds", 10), ct);
            if (result.Status == BatchDisasterStatus.RetryableFailure) await channel.BasicNackAsync(delivery.DeliveryTag, false, false, ct);
            else await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            return;
        }
    }
}
