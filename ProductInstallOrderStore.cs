using System.IO;
using System.Text.Json;

namespace MCPanel;

internal sealed class ProductInstallOrderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _storeFile;
    private ProductInstallOrderDocument? _document;

    public ProductInstallOrderStore(string? storeFile = null)
    {
        _storeFile = storeFile ?? Path.Combine(
            ComponentPaths.ProductOrderStateRoot,
            "install-order.json");
    }

    public void EnsureInstalled(IEnumerable<ProductInstallOrderCandidate> candidates)
    {
        lock (_syncRoot)
        {
            var document = Load();
            var knownIds = new HashSet<string>(
                document.Entries.Select(entry => entry.ProductId),
                StringComparer.OrdinalIgnoreCase);
            var missing = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ProductId) && !knownIds.Contains(candidate.ProductId))
                .GroupBy(candidate => candidate.ProductId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(candidate => candidate.InstalledAtUtc).First())
                .OrderBy(candidate => candidate.InstalledAtUtc)
                .ThenBy(candidate => candidate.ProductId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (missing.Length == 0)
            {
                return;
            }

            foreach (var candidate in missing)
            {
                document.Entries.Add(new ProductInstallOrderEntry
                {
                    ProductId = candidate.ProductId,
                    Sequence = document.NextSequence++,
                    InstalledAtUtc = candidate.InstalledAtUtc
                });
            }

            Save(document);
        }
    }

    public void RecordInstalled(string productId, DateTime? installedAtUtc = null)
    {
        EnsureInstalled([
            new ProductInstallOrderCandidate(
                productId,
                installedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow)
        ]);
    }

    public void Remove(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
        {
            return;
        }

        lock (_syncRoot)
        {
            var document = Load();
            var removed = document.Entries.RemoveAll(entry =>
                entry.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Save(document);
            }
        }
    }

    public int GetSequence(string productId)
    {
        lock (_syncRoot)
        {
            return Load().Entries
                .FirstOrDefault(entry => entry.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase))
                ?.Sequence ?? int.MaxValue;
        }
    }

    private ProductInstallOrderDocument Load()
    {
        if (_document is not null)
        {
            return _document;
        }

        try
        {
            if (File.Exists(_storeFile))
            {
                var json = File.ReadAllText(_storeFile);
                _document = JsonSerializer.Deserialize<ProductInstallOrderDocument>(json, JsonOptions);
            }
        }
        catch
        {
            _document = null;
        }

        _document ??= new ProductInstallOrderDocument();
        _document.Entries ??= [];
        var highestSequence = _document.Entries.Count == 0
            ? 0
            : _document.Entries.Max(entry => entry.Sequence);
        _document.NextSequence = Math.Max(_document.NextSequence, highestSequence + 1);
        return _document;
    }

    private void Save(ProductInstallOrderDocument document)
    {
        var directory = Path.GetDirectoryName(_storeFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(_storeFile, JsonSerializer.Serialize(document, JsonOptions));
    }

    private sealed class ProductInstallOrderDocument
    {
        public int NextSequence { get; set; } = 1;
        public List<ProductInstallOrderEntry> Entries { get; set; } = [];
    }

    private sealed class ProductInstallOrderEntry
    {
        public string ProductId { get; set; } = string.Empty;
        public int Sequence { get; set; }
        public DateTime InstalledAtUtc { get; set; }
    }
}

internal sealed record ProductInstallOrderCandidate(string ProductId, DateTime InstalledAtUtc);
