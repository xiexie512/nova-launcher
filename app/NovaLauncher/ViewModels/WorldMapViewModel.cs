using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NBT.Tags;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>
/// 世界存档项。
/// </summary>
public sealed partial class WorldSaveItem : ObservableObject
{
    public string Name { get; }
    public string Path { get; }
    public string VersionId { get; }
    public long Size { get; }
    public DateTime LastPlayed { get; }

    [ObservableProperty]
    private bool _isSelected;

    public WorldSaveItem(string name, string path, string versionId, long size, DateTime lastPlayed)
    {
        Name = name;
        Path = path;
        VersionId = versionId;
        Size = size;
        LastPlayed = lastPlayed;
    }

    public string SizeText => Size >= 1_000_000_000 ? $"{Size / 1_000_000_000.0:F1} GB" : $"{Size / 1_000_000.0:F0} MB";
}

/// <summary>
/// 区块坐标。
/// </summary>
public readonly record struct ChunkCoord(int X, int Z);

/// <summary>
/// 群系类型（对应 MC 1.18+ biome registry）。
/// </summary>
public enum BiomeType
{
    Unknown = -1,
    Plains = 1,
    Forest = 4,
    Taiga = 5,
    Swamp = 6,
    River = 7,
    Desert = 2,
    ExtremeHills = 3,
    MushroomIsland = 14,
    Beach = 16,
    Jungle = 21,
    Savanna = 35,
    Badlands = 39,
    Ocean = 0,
    DeepOcean = 24,
    FrozenOcean = 10,
    FrozenRiver = 11,
    SnowyPlains = 12,
    SnowyTaiga = 30,
}

/// <summary>
/// 结构类型。
/// </summary>
public enum StructureType
{
    Village,
    Temple,
    Monument,
    Mansion,
    Stronghold,
    Mineshaft,
    RuinedPortal,
    BuriedTreasure,
    Shipwreck,
    OceanRuin,
    PillagerOutpost,
    AncientCity,
    TrailRuins,
}

/// <summary>
/// 矿石类型。
/// </summary>
public enum OreType
{
    Coal,
    Iron,
    Copper,
    Gold,
    Redstone,
    Lapis,
    Diamond,
    Emerald,
    NetherGold,
    NetherQuartz,
    AncientDebris,
}

/// <summary>
/// 世界地图查看器：解析 region 文件（.mca），提取群系/结构/矿石数据并渲染。
/// </summary>
public sealed partial class WorldMapViewModel : ObservableObject
{
    private string _selectedWorldPath = "";
    private string _status = "";
    private bool _busy;
    private int _renderScale = 1;
    private int _viewOffsetX = 0;
    private int _viewOffsetZ = 0;

    // 显示开关
    private bool _showBiomes = true;
    private bool _showStructures = true;
    private bool _showOres = false;
    private bool _showGrid = true;

    public ObservableCollection<WorldSaveItem> Worlds { get; } = new();
    public ObservableCollection<ChunkInfo> ChunkData { get; } = new();

    public string SelectedWorldPath
    {
        get => _selectedWorldPath;
        set
        {
            if (SetProperty(ref _selectedWorldPath, value))
            {
                LoadWorldAsync();
            }
        }
    }

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public int RenderScale
    {
        get => _renderScale;
        set => SetProperty(ref _renderScale, Math.Clamp(value, 1, 4));
    }

    public int ViewOffsetX { get => _viewOffsetX; set => SetProperty(ref _viewOffsetX, value); }
    public int ViewOffsetZ { get => _viewOffsetZ; set => SetProperty(ref _viewOffsetZ, value); }

    public bool ShowBiomes { get => _showBiomes; set => SetProperty(ref _showBiomes, value); }
    public bool ShowStructures { get => _showStructures; set => SetProperty(ref _showStructures, value); }
    public bool ShowOres { get => _showOres; set => SetProperty(ref _showOres, value); }
    public bool ShowGrid { get => _showGrid; set => SetProperty(ref _showGrid, value); }

    public int ChunkCount => ChunkData.Count;

    public WorldMapViewModel()
    {
        RefreshWorldList();
    }

    /// <summary>刷新世界列表。</summary>
    [RelayCommand]
    public void RefreshWorldList()
    {
        Worlds.Clear();
        var savesDir = AppPaths.Saves;
        if (!Directory.Exists(savesDir)) return;

        foreach (var dir in Directory.GetDirectories(savesDir))
        {
            var levelDat = Path.Combine(dir, "level.dat");
            if (!File.Exists(levelDat)) continue;

            var fi = new FileInfo(dir);
            var size = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

            Worlds.Add(new WorldSaveItem(
                Path.GetFileName(dir),
                dir,
                "",
                size,
                fi.LastWriteTime
            ));
        }

        if (Worlds.Count > 0 && string.IsNullOrEmpty(SelectedWorldPath))
            SelectedWorldPath = Worlds[0].Path;
    }

    /// <summary>加载选中的世界（解析 region 文件）。</summary>
    private async void LoadWorldAsync()
    {
        if (string.IsNullOrEmpty(SelectedWorldPath)) return;

        Busy = true;
        Status = "解析 region 文件...";
        ChunkData.Clear();

        try
        {
            await Task.Run(() => ParseRegionFiles(SelectedWorldPath));
            Status = $"加载完成：{ChunkData.Count} 个区块";
        }
        catch (Exception e)
        {
            Status = "加载失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>解析 region 文件夹，使用 NBT.NET 读取 .mca 文件。.</summary>
    private void ParseRegionFiles(string worldPath)
    {
        var regionDir = Path.Combine(worldPath, "region");
        if (!Directory.Exists(regionDir)) return;

        var regionFiles = Directory.GetFiles(regionDir, "r.*.mca");
        var parsedCount = 0;

        foreach (var file in regionFiles)
        {
            try
            {
                var chunks = ParseRegionFile(file);
                foreach (var chunk in chunks)
                    ChunkData.Add(chunk);
                parsedCount++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse {file}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"Parsed {parsedCount} region files, {ChunkData.Count} chunks total");
    }

    /// <summary>
    /// 解析单个 .mca region 文件（使用 NBT.NET）。
    /// .mca 格式：4KB 头部（4096 字节，每个区块 4 字节偏移+1 字节扇区数+3 字节时间戳）+ 区块数据（Zlib 压缩的 NBT）。
    /// </summary>
    private List<ChunkInfo> ParseRegionFile(string filePath)
    {
        var result = new List<ChunkInfo>();

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var header = new byte[4096];
            fs.ReadExactly(header);

            var fileName = Path.GetFileNameWithoutExtension(filePath); // r.-1.-1
            var parts = fileName.Split('.');
            if (parts.Length < 3 || !int.TryParse(parts[1], out int rx) || !int.TryParse(parts[2], out int rz))
                return result;

            // 每个 region 32x32 区块，共 1024 个位置
            for (int i = 0; i < 1024; i++)
            {
                int offsetIndex = i * 4;
                // 偏移量：3 字节大端序扇区偏移 + 1 字节扇区数
                int sectorOffset = (header[offsetIndex] << 16) | (header[offsetIndex + 1] << 8) | header[offsetIndex + 2];
                int sectorCount = header[offsetIndex + 3];

                if (sectorCount == 0) continue; // 空区块

                // 每个扇区 4KB
                long dataOffset = sectorOffset * 4096;
                if (dataOffset >= fs.Length) continue;

                fs.Seek(dataOffset, SeekOrigin.Begin);

                // 读取长度（4 字节大端序）
                var lengthBytes = new byte[4];
                if (fs.Read(lengthBytes, 0, 4) != 4) continue;
                int length = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];

                // 读取压缩类型（1 字节：1=GZIP, 2=ZLIB）
                var compressionByte = new byte[1];
                if (fs.Read(compressionByte, 0, 1) != 1) continue;
                var compressionType = compressionByte[0];

                // 读取压缩数据
                var compressedData = new byte[length - 1];
                if (fs.Read(compressedData, 0, compressedData.Length) != compressedData.Length) continue;

                // 解压
                byte[] nbtData;
                try
                {
                    nbtData = compressionType switch
                    {
                        1 => DecompressGzip(compressedData),
                        2 => DecompressZlib(compressedData),
                        _ => compressedData
                    };
                }
                catch
                {
                    continue;
                }

                // 解析 NBT
                var chunk = ParseChunkNbt(nbtData, rx, rz, i);
                if (chunk != null) result.Add(chunk);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error parsing region file {filePath}: {ex.Message}");
        }

        return result;
    }

    private static byte[] DecompressZlib(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// 从 NBT 数据中提取区块信息。
    /// </summary>
    private ChunkInfo? ParseChunkNbt(byte[] nbtData, int rx, int rz, int chunkIndex)
    {
        try
        {
            var nbt = NBT.Tags.Tag.LoadFromBuffer(nbtData) as CompoundTag;
            if (nbt == null) return null;

            // 获取 Level 标签
            if (!nbt.TryGet("Level", out var levelTag) || levelTag is not CompoundTag level) return null;

            // 区块坐标
            int x = level.GetInt("xPos");
            int z = level.GetInt("zPos");

            // 验证坐标匹配 region 文件
            int expectedRx = Math.Floor(x / 32.0);
            int expectedRz = Math.Floor(z / 32.0);
            if (expectedRx != rx || expectedRz != rz) return null;

            // 群系数据（1.18+ 存储在 biomes 数组中，每个 4x4x4 体素一个）
            int biomeId = 0;
            if (level.TryGet("biomes", out var biomesTag) && biomesTag is ListTag biomesList && biomesList.Count > 0)
            {
                // 取第一个群系作为该区块主群系
                biomeId = (biomesList[0] as IntTag)?.Value ?? 0;
            }
            else if (level.TryGet("Biomes", out var oldBiomesTag) && oldBiomesTag is IntArrayTag oldBiomes)
            {
                // 旧版本格式：256 个 int
                biomeId = oldBiomes.Value.Length > 0 ? oldBiomes.Value[0] : 0;
            }

            // 结构数据
            bool hasStructure = false;
            StructureType? structureType = null;

            if (level.TryGet("Structures", out var structuresTag) && structuresTag is CompoundTag structures)
            {
                // Structures/Starts 列表
                if (structures.TryGet("Starts", out var startsTag) && startsTag is ListTag startsList)
                {
                    foreach (var start in startsList)
                    {
                        if (start is CompoundTag startCompound && startCompound.TryGet("id", out var idTag))
                        {
                            var id = (idTag as StringTag)?.Value ?? "";
                            if (!string.IsNullOrEmpty(id))
                            {
                                hasStructure = true;
                                structureType = ParseStructureType(id);
                                break;
                            }
                        }
                    }
                }
                // 兼容旧版本：Structures 直接包含结构名
                if (!hasStructure)
                {
                    foreach (var key in structures.Keys)
                    {
                        if (!key.StartsWith("data_") && !key.Equals("References"))
                        {
                            hasStructure = true;
                            structureType = ParseStructureType(key);
                            break;
                        }
                    }
                }
            }

            // 矿石统计（扫描区块段的方块实体/区块数据）
            var oreCounts = CountOresInChunk(level);

            return new ChunkInfo
            {
                ChunkX = x,
                ChunkZ = z,
                BiomeId = biomeId,
                HasStructure = hasStructure,
                StructureType = structureType,
                OreCounts = oreCounts
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error parsing chunk NBT: {ex.Message}");
            return null;
        }
    }

    private static StructureType? ParseStructureType(string id)
    {
        id = id.ToLowerInvariant();
        if (id.Contains("village")) return StructureType.Village;
        if (id.Contains("temple") || id.Contains("pyramid")) return StructureType.Temple;
        if (id.Contains("monument")) return StructureType.Monument;
        if (id.Contains("mansion")) return StructureType.Mansion;
        if (id.Contains("stronghold")) return StructureType.Stronghold;
        if (id.Contains("mineshaft")) return StructureType.Mineshaft;
        if (id.Contains("ruined_portal")) return StructureType.RuinedPortal;
        if (id.Contains("buried_treasure")) return StructureType.BuriedTreasure;
        if (id.Contains("shipwreck")) return StructureType.Shipwreck;
        if (id.Contains("ocean_ruin")) return StructureType.OceanRuin;
        if (id.Contains("pillager_outpost")) return StructureType.PillagerOutpost;
        if (id.Contains("ancient_city")) return StructureType.AncientCity;
        if (id.Contains("trail_ruins")) return StructureType.TrailRuins;
        return null;
    }

    private static Dictionary<OreType, int> CountOresInChunk(CompoundTag level)
    {
        var counts = new Dictionary<OreType, int>();

        // 扫描 sections（区块段，每段 16x16x16）
        if (!level.TryGet("sections", out var sectionsTag) || sectionsTag is not ListTag sectionsList)
            return counts;

        // 矿石方块 ID 映射（Java Edition 状态 ID 大致范围，实际应用中需完整映射表）
        var oreBlockIds = new HashSet<int>
        {
            16, 17,    // Coal Ore / Deepslate Coal Ore
            15, 170,   // Iron Ore / Deepslate Iron Ore
            736, 737,  // Copper Ore / Deepslate Copper Ore
            14, 169,   // Gold Ore / Deepslate Gold Ore
            152, 735,  // Redstone Ore / Deepslate Redstone Ore
            153, 154,  // Lapis Ore / Deepslate Lapis Ore
            56, 174,   // Diamond Ore / Deepslate Diamond Ore
            129,       // Emerald Ore
            // Nether ores
            757,       // Nether Gold Ore
            755,       // Nether Quartz Ore
            756,       // Ancient Debris
        };

        foreach (var sectionTag in sectionsList)
        {
            if (sectionTag is not CompoundTag section) continue;

            // 获取方块状态调色板
            if (!section.TryGet("palette", out var paletteTag) || paletteTag is not ListTag paletteList) continue;
            if (!section.TryGet("block_states", out var statesTag) || statesTag is not LongArrayTag statesArray) continue;

            var palette = new List<int>();
            foreach (var entry in paletteList)
            {
                if (entry is CompoundTag blockEntry && blockEntry.TryGet("Name", out var nameTag))
                {
                    var name = (nameTag as StringTag)?.Value ?? "";
                    // 简单映射：从名称提取 ID（实际应用需要完整的方块状态映射）
                    var id = GetBlockIdFromName(name);
                    palette.Add(id);
                }
            }

            // 解码 LongArray 中的方块状态索引
            var states = statesArray.Value;
            int bitsPerBlock = Math.Max(4, (int)Math.Ceiling(Math.Log2(palette.Count)));
            int blocksPerLong = 64 / bitsPerBlock;
            long mask = (1L << bitsPerBlock) - 1;

            for (int i = 0; i < states.Length; i++)
            {
                long value = states[i];
                for (int b = 0; b < blocksPerLong; b++)
                {
                    int index = (int)(value & mask);
                    value >>= bitsPerBlock;
                    if (index < palette.Count)
                    {
                        int blockId = palette[index];
                        if (oreBlockIds.Contains(blockId))
                        {
                            var oreType = GetOreTypeFromBlockId(blockId);
                            if (oreType.HasValue)
                                counts[oreType.Value] = counts.GetValueOrDefault(oreType.Value) + 1;
                        }
                    }
                }
            }
        }

        return counts;
    }

    private static int GetBlockIdFromName(string name)
    {
        // 简化映射：实际应用中需完整的方块注册表
        return name switch
        {
            "minecraft:coal_ore" => 16,
            "minecraft:deepslate_coal_ore" => 17,
            "minecraft:iron_ore" => 15,
            "minecraft:deepslate_iron_ore" => 170,
            "minecraft:copper_ore" => 736,
            "minecraft:deepslate_copper_ore" => 737,
            "minecraft:gold_ore" => 14,
            "minecraft:deepslate_gold_ore" => 169,
            "minecraft:redstone_ore" => 152,
            "minecraft:deepslate_redstone_ore" => 735,
            "minecraft:lapis_ore" => 153,
            "minecraft:deepslate_lapis_ore" => 154,
            "minecraft:diamond_ore" => 56,
            "minecraft:deepslate_diamond_ore" => 174,
            "minecraft:emerald_ore" => 129,
            "minecraft:nether_gold_ore" => 757,
            "minecraft:nether_quartz_ore" => 755,
            "minecraft:ancient_debris" => 756,
            _ => name.GetHashCode() // fallback
        };
    }

    private static OreType? GetOreTypeFromBlockId(int id)
    {
        return id switch
        {
            16 or 17 => OreType.Coal,
            15 or 170 => OreType.Iron,
            736 or 737 => OreType.Copper,
            14 or 169 => OreType.Gold,
            152 or 735 => OreType.Redstone,
            153 or 154 => OreType.Lapis,
            56 or 174 => OreType.Diamond,
            129 => OreType.Emerald,
            757 => OreType.NetherGold,
            755 => OreType.NetherQuartz,
            756 => OreType.AncientDebris,
            _ => null
        };
    }

    /// <summary>导出当前地图视图为 PNG 文件。.</summary>
    [RelayCommand]
    public void ExportMap()
    {
        // 实际导出由 View 侧的 WriteableBitmap.Save 实现
        Status = "请在界面点击导出按钮（View 层实现保存）";
    }

    /// <summary>居中视图到 (0,0)。.</summary>
    [RelayCommand]
    public void CenterView()
    {
        ViewOffsetX = 0;
        ViewOffsetZ = 0;
    }
}

/// <summary>
/// 区块信息（用于地图渲染）。
/// </summary>
public sealed class ChunkInfo
{
    public int ChunkX { get; init; }
    public int ChunkZ { get; init; }
    public int BiomeId { get; init; }
    public bool HasStructure { get; init; }
    public StructureType? StructureType { get; init; }
    public Dictionary<OreType, int> OreCounts { get; init; } = new();

    public string CoordText => $"{ChunkX}, {ChunkZ}";
    public string BiomeName => BiomeId switch
    {
        1 => "平原", 4 => "森林", 5 => "针叶林", 6 => "沼泽",
        2 => "沙漠", 3 => "极端山地", 14 => "蘑菇岛", 16 => "海滩",
        21 => "丛林", 35 => "稀树草原", 39 => "恶地",
        0 => "海洋", 24 => "深海", 10 => "冰冻海洋", 11 => "冰冻河流",
        12 => "雪原", 30 => "雪地针叶林",
        _ => $"未知({BiomeId})"
    };
}