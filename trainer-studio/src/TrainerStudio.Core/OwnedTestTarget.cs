using System.Buffers.Binary;

namespace TrainerStudio.Core;

/// <summary>
/// Self-owned deterministic target used for all first-release scans and
/// trainer tests. It represents a small documented game memory region and is
/// deliberately the only target exposed by the starter application.
/// </summary>
public sealed class OwnedTestTarget : IApprovedMemoryTarget
{
    /// <summary>Default module base for the first test session.</summary>
    public const long BaseAddress = 0x0000_0001_0000_0000;
    public const string ModuleName = "TrainerStudio.OwnedTestGame";
    public const string TargetId = "trainer-studio-owned-test-game";
    public const int CellCount = 256;
    public const int CurrencyIndex = 37;
    public const int HealthIndex = 66;
    public const int AmmoIndex = 104;
    public const int StaminaIndex = 151;
    public const int TimerIndex = 202;

    private readonly int[] _cells = new int[CellCount];
    private readonly long _moduleBaseAddress;
    private int _currencyActions;
    private int _ammoActions;

    public OwnedTestTarget(
        string version = "1.0.0",
        long? moduleBaseAddress = null,
        string? buildFingerprint = null)
    {
        _moduleBaseAddress = moduleBaseAddress ?? BaseAddress;
        Identity = new TargetIdentity(
            TargetId,
            "Trainer Studio – eget testspil",
            version,
            "x64",
            ModuleName,
            buildFingerprint ?? $"owned-testgame:{version}");
        Reset();
    }

    public TargetIdentity Identity { get; }

    public long ModuleBaseAddress => _moduleBaseAddress;

    public long CurrencyAddress => AddressFor(CurrencyIndex);

    public long HealthAddress => AddressFor(HealthIndex);

    public long AmmoAddress => AddressFor(AmmoIndex);

    public long StaminaAddress => AddressFor(StaminaIndex);

    public long TimerAddress => AddressFor(TimerIndex);

    public int Currency => _cells[CurrencyIndex];

    public int Health => _cells[HealthIndex];

    public int Ammo => _cells[AmmoIndex];

    public int Stamina => _cells[StaminaIndex];

    public int Timer => _cells[TimerIndex];

    public bool TryResolveModuleBase(string moduleName, out long baseAddress)
    {
        if (StringComparer.Ordinal.Equals(moduleName, ModuleName))
        {
            baseAddress = _moduleBaseAddress;
            return true;
        }

        baseAddress = 0;
        return false;
    }

    public IReadOnlyList<MemoryRegion> Snapshot()
    {
        var bytes = new byte[CellCount * sizeof(int)];
        for (var i = 0; i < CellCount; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int)), _cells[i]);
        }

        return [new MemoryRegion(_moduleBaseAddress, bytes, "OwnedTestGame.data")];
    }

    public bool TryRead(long address, ScanDataType type, out ScanValue value)
    {
        var offset = address - _moduleBaseAddress;
        if (offset < 0 || offset > CellCount * sizeof(int) - ValueCodec.SizeOf(type))
        {
            value = default;
            return false;
        }

        var bytes = Snapshot()[0].Bytes;
        return ValueCodec.TryRead(bytes.AsSpan(checked((int)offset)), type, out value);
    }

    public bool TryWrite(long address, ScanValue value, out string error)
    {
        error = string.Empty;
        if (value.Type != ScanDataType.Int32)
        {
            error = "Det første testspil tillader kun Int32-værdier.";
            return false;
        }

        var offset = address - _moduleBaseAddress;
        if (offset < 0 || offset % sizeof(int) != 0 || offset >= CellCount * sizeof(int))
        {
            error = "Adressen findes ikke i det ejede testspil.";
            return false;
        }

        var valueAsInt = BinaryPrimitives.ReadInt32LittleEndian(value.Bytes);
        if (valueAsInt is < 0 or > 1_000_000)
        {
            error = "Testspillet accepterer værdier fra 0 til 1.000.000.";
            return false;
        }

        _cells[checked((int)(offset / sizeof(int)))] = valueAsInt;
        return true;
    }

    public void Reset()
    {
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = 10_000 + i * 101;
        }

        _cells[CurrencyIndex] = 50;
        _cells[HealthIndex] = 100;
        _cells[AmmoIndex] = 12;
        _cells[StaminaIndex] = 100;
        _cells[TimerIndex] = 300;
        _cells[8] = _cells[91] = 50;
        _cells[24] = _cells[188] = 100;
        _cells[42] = _cells[122] = 12;
        _currencyActions = 0;
        _ammoActions = 0;
    }

    public void CollectCurrency()
    {
        _cells[CurrencyIndex] = checked(_cells[CurrencyIndex] + 25);
        _currencyActions++;
        _cells[8] = _currencyActions == 1 ? _cells[CurrencyIndex] : _cells[CurrencyIndex] + 1;
    }

    public void SpendCurrency()
    {
        _cells[CurrencyIndex] = Math.Max(0, _cells[CurrencyIndex] - 10);
        _currencyActions++;
        _cells[8] = _currencyActions == 1 ? _cells[CurrencyIndex] : _cells[CurrencyIndex] + 1;
    }

    public void TakeDamage()
    {
        _cells[HealthIndex] = Math.Max(0, _cells[HealthIndex] - 15);
        _cells[24] = _cells[HealthIndex];
    }

    public void Heal()
    {
        _cells[HealthIndex] = Math.Min(100, _cells[HealthIndex] + 10);
        _cells[24] = _cells[HealthIndex];
    }

    public void Fire()
    {
        _cells[AmmoIndex] = Math.Max(0, _cells[AmmoIndex] - 1);
        _ammoActions++;
        _cells[42] = _ammoActions == 1 ? _cells[AmmoIndex] : _cells[AmmoIndex] + 1;
    }

    public void Reload()
    {
        _cells[AmmoIndex] = Math.Min(1_000, _cells[AmmoIndex] + 5);
        _ammoActions++;
        _cells[42] = _ammoActions == 1 ? _cells[AmmoIndex] : _cells[AmmoIndex] + 1;
    }

    public void DrainStamina() => _cells[StaminaIndex] = Math.Max(0, _cells[StaminaIndex] - 20);

    public void TickTimer() => _cells[TimerIndex] = Math.Max(0, _cells[TimerIndex] - 1);

    public long AddressFor(int index) => checked(_moduleBaseAddress + index * sizeof(int));
}
