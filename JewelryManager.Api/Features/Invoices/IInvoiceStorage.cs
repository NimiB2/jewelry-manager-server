namespace JewelryManager.Api.Features.Invoices;

/// <summary>
/// Where invoice files live. Files are private: there is no public URL, they are only ever read
/// back through the authenticated API. Swapping the local disk for cloud storage means a new
/// implementation of this interface, nothing else.
/// </summary>
public interface IInvoiceStorage
{
    Task SaveAsync(Guid businessId, string storedName, Stream content);

    /// <summary>Null when the file is gone.</summary>
    Task<Stream?> OpenReadAsync(Guid businessId, string storedName);

    Task DeleteAsync(Guid businessId, string storedName);
}
