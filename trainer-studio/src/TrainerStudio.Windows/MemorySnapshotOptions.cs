namespace TrainerStudio.Windows;

public sealed record MemorySnapshotOptions(
    long MaxBytes = 256L * 1024 * 1024,
    int ChunkBytes = 1024 * 1024,
    bool WritableOnly = true,
    bool IncludePrivate = true,
    bool IncludeImages = true,
    bool IncludeMapped = false,
    long StartAddress = 0,
    long EndAddressExclusive = long.MaxValue,
    bool PrioritizeModules = false)
{
    public void Validate()
    {
        if (MaxBytes is < 1 or > 64L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), "Læseloftet skal være mellem 1 byte og 64 GiB.");
        }

        if (ChunkBytes is < 4096 or > 16 * 1024 * 1024 || ChunkBytes % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkBytes), "Chunkstørrelsen skal være 4 KiB–16 MiB og delelig med 8.");
        }

        if (StartAddress < 0 || EndAddressExclusive <= StartAddress)
        {
            throw new ArgumentOutOfRangeException(nameof(StartAddress), "Adresseintervallet er ugyldigt.");
        }

        if (!IncludePrivate && !IncludeImages && !IncludeMapped)
        {
            throw new ArgumentException("Vælg mindst én regionstype.");
        }
    }
}

public sealed record MemorySnapshotReport(long BytesRead, int ReadRegions, int SkippedReadCount, bool WasTruncated, string Scope);
