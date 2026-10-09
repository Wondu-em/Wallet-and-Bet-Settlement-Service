namespace Wallet.Application.Outbox;

public interface IOutboxWriter
{
    void Add(string type, object payload);
}
