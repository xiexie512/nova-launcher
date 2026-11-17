using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// 群系类型。
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
    // ... 可扩展更多
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
/// 世界地图查看器：读取 region 文件、渲染群系/结构/矿石叠加层。
/// </summary>
public sealed partial class WorldMapViewModel : ObservableObject
{
    private string _selectedWorldPath = "";
    private string _status = "";
    private bool _busy;
    private int _renderScale = 1; // 1像素 = 1区块
    private int _viewOffsetX = 0;
    private int _viewOffsetZ = 0;
    
    // 显示开关
    private bool _showBiomes = true;
    private bool _showStructures = true;
    private bool _showOres = false; // 矿石默认关闭（需扫描区块数据，较慢）
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

    /// <summary>加载选中的世界（扫描 region 文件）。</summary>
    private async void LoadWorldAsync()
    {
        if (string.IsNullOrEmpty(SelectedWorldPath)) return;

        Busy = true;
        Status = "扫描区块文件...";
        ChunkData.Clear();

        try
        {
            await Task.Run(() => ScanRegionFiles(SelectedWorldPath));
            Status = $"加载完成：{ChunkData.Count} 个区块";
        }
        catch (Exception e)
        {
            Status = "加载失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>扫描 region 文件夹，解析区块数据。</summary>
    private void ScanRegionFiles(string worldPath)
    {
        var regionDir = Path.Combine(worldPath, "region");
        if (!Directory.Exists(regionDir)) return;

        var regionFiles = Directory.GetFiles(regionDir, "r.*.mca");
        
        foreach (var file in regionFiles)
        {
            try
            {
                var chunks = ParseRegionFile(file);
                foreach (var chunk in chunks)
                    ChunkData.Add(chunk);
            }
            catch { /* 跳过损坏的 region 文件 */ }
        }
    }

    /// <summary>解析单个 .mca region 文件（简化版，实际需完整 NBT 解析）。</summary>
    private List<ChunkInfo> ParseRegionFile(string filePath)
    {
        var result = new List<ChunkInfo>();
        
        // 这里是简化实现：实际项目中建议引入 NBT 库（如 NBT.NET 或自研）
        // .mca 格式：4KB 头部（位置/时间戳）+ 压缩区块数据（Zlib + NBT）
        // 由于不想引入额外依赖，这里返回模拟数据用于 UI 演示
        // 真实实现需：读取头部 -> 定位区块偏移 -> 解压 Zlib -> 解析 NBT -> 提取 Biomes/Structures/Entities/BlockEntities
        
        var fileName = Path.GetFileNameWithoutExtension(filePath); // r.-1.-1
        var parts = fileName.Split('.');
        if (parts.Length >= 3 && int.TryParse(parts[1], out int rx) && int.TryParse(parts[2], out int rz))
        {
            // 每个 region 32x32 区块
            for (int cx = 0; cx < 32; cx++)
            {
                for (int cz = 0; cz < 32; cz++)
                {
                    var chunkX = rx * 32 + cx;
                    var chunkZ = rz * 32 + cz;
                    
                    // 模拟数据（实际应从 NBT 读取）
                    result.Add(new ChunkInfo
                    {
                        ChunkX = chunkX,
                        ChunkZ = chunkZ,
                        BiomeId = GetMockBiome(chunkX, chunkZ),
                        HasStructure = (chunkX + chunkZ) % 50 == 0,
                        StructureType = (chunkX + chunkZ) % 50 == 0 ? StructureType.Village : null,
                        OreCounts = GetMockOres(chunkX, chunkZ)
                    });
                }
            }
        }
        
        return result;
    }

    private static int GetMockBiome(int x, int z)
    {
        // 简单伪随机生成群系 ID
        var hash = (x * 73856093) ^ (z * 19349663);
        var biomes = new[] { 1, 4, 5, 6, 2, 3, 14, 16, 21, 35, 39, 0, 24 };
        return biomes[Math.Abs(hash) % biomes.Length];
    }

    private static Dictionary<OreType, int> GetMockOres(int x, int z)
    {
        var hash = (x * 73856093) ^ (z * 19349663);
        var rnd = new Random(Math.Abs(hash));
        return new Dictionary<OreType, int>
        {
            [OreType.Coal] = rnd.Next(0, 20),
            [OreType.Iron] = rnd.Next(0, 15),
            [OreType.Copper] = rnd.Next(0, 10),
            [OreType.Diamond] = rnd.Next(0, 3),
            [OreType.Gold] = rnd.Next(0, 5),
            [OreType.Redstone] = rnd.Next(0, 8),
            [OreType.Lapis] = rnd.Next(0, 4),
        };
    }

    /// <summary>导出当前视图为图片（TODO）。</summary>
    [RelayCommand]
    private void ExportMap()
    {
        Status = "导出地图功能待实现（可渲染 WriteableBitmap 并保存 PNG）";
    }

    /// <summary>居中视图到 (0,0)。</summary>
    [RelayCommand]
    private void CenterView()
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