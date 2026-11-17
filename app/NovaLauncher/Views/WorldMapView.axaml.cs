using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using NovaLauncher.ViewModels;

namespace NovaLauncher.Views;

public partial class WorldMapView : UserControl
{
    private WriteableBitmap? _bitmap;
    private readonly object _renderLock = new();

    public WorldMapView()
    {
        InitializeComponent();
        
        // 监听数据变化重绘
        DataContextChanged += (_, _) => 
        {
            if (DataContext is WorldMapViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(WorldMapViewModel.ChunkData) 
                        or nameof(WorldMapViewModel.ShowBiomes)
                        or nameof(WorldMapViewModel.ShowStructures)
                        or nameof(WorldMapViewModel.ShowOres)
                        or nameof(WorldMapViewModel.ShowGrid)
                        or nameof(WorldMapViewModel.RenderScale)
                        or nameof(WorldMapViewModel.ViewOffsetX)
                        or nameof(WorldMapViewModel.ViewOffsetZ))
                    {
                        RenderMap();
                    }
                };
            }
        };

        // 画布大小变化时重绘
        this.GetObservable(BoundsProperty).Subscribe(_ => RenderMap());
    }

    /// <summary>导出按钮点击事件。</summary>
    private async void OnExportMapClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_bitmap == null) return;

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出地图为 PNG",
                DefaultExtension = "png",
                SuggestedFileName = $"worldmap_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                }
            });

            if (file == null) return;

            // 使用 WriteableBitmap 保存为 PNG
            using var stream = await file.OpenWriteAsync();
            _bitmap.Save(stream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Export error: {ex.Message}");
        }
    }

    private void RenderMap()
    {
        if (DataContext is not WorldMapViewModel vm) return;
        if (MapCanvas.Bounds.Width <= 0 || MapCanvas.Bounds.Height <= 0) return;

        lock (_renderLock)
        {
            try
            {
                var width = (int)MapCanvas.Bounds.Width;
                var height = (int)MapCanvas.Bounds.Height;

                // 创建或重用 WriteableBitmap
                if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
                {
                    _bitmap = new WriteableBitmap(
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                }

                using (var buf = _bitmap.Lock())
                {
                    // 清空背景
                    var ptr = buf.Address;
                    var stride = buf.RowBytes;
                    unsafe
                    {
                        var p = (byte*)ptr;
                        for (int y = 0; y < height; y++)
                        {
                            var row = p + y * stride;
                            for (int x = 0; x < width; x++)
                            {
                                // 深色背景 #0D1117
                                row[x * 4 + 0] = 0x17; // B
                                row[x * 4 + 1] = 0x11; // G
                                row[x * 4 + 2] = 0x0D; // R
                                row[x * 4 + 3] = 0xFF; // A
                            }
                        }
                    }

                    // 计算可见区块范围
                    var scale = vm.RenderScale; // 1像素 = 1区块 * scale
                    var centerX = width / 2 - vm.ViewOffsetX * scale;
                    var centerZ = height / 2 - vm.ViewOffsetZ * scale;

                    var minChunkX = (int)Math.Floor((-centerX) / (double)scale);
                    var maxChunkX = (int)Math.Ceiling((width - centerX) / (double)scale);
                    var minChunkZ = (int)Math.Floor((-centerZ) / (double)scale);
                    var maxChunkZ = (int)Math.Ceiling((height - centerZ) / (double)scale);

                    // 绘制区块
                    foreach (var chunk in vm.ChunkData)
                    {
                        var px = centerX + chunk.ChunkX * scale;
                        var pz = centerZ + chunk.ChunkZ * scale;

                        if (px < -scale || px >= width + scale || pz < -scale || pz >= height + scale)
                            continue;

                        DrawChunk(buf, px, pz, scale, chunk, vm);
                    }

                    // 绘制网格
                    if (vm.ShowGrid)
                    {
                        DrawGrid(buf, width, height, centerX, centerZ, scale);
                    }

                    // 绘制坐标原点标记
                    DrawOrigin(buf, centerX, centerZ, scale);
                }

                // 更新 Canvas 背景
                MapCanvas.Background = new ImageBrush(_bitmap) { Stretch = Stretch.None };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
            }
        }
    }

    private void DrawChunk(WriteableBitmap.Lock buf, double px, double pz, int scale, ChunkInfo chunk, WorldMapViewModel vm)
    {
        unsafe
        {
            var ptr = (byte*)buf.Address;
            var stride = buf.RowBytes;
            var width = buf.Size.Width;
            var height = buf.Size.Height;

            int x0 = (int)Math.Round(px);
            int z0 = (int)Math.Round(pz);
            int size = scale;

            // 群系颜色
            if (vm.ShowBiomes)
            {
                var color = GetBiomeColor(chunk.BiomeId);
                FillRect(ptr, stride, width, height, x0, z0, size, color);
            }

            // 结构标记
            if (vm.ShowStructures && chunk.HasStructure)
            {
                var color = Color.FromRgb(0xFF, 0xD7, 0x00); // 金色
                DrawRect(ptr, stride, width, height, x0, z0, size, color, 2);
                
                // 结构类型小图标（中心点）
                if (chunk.StructureType.HasValue)
                {
                    var cx = x0 + size / 2;
                    var cz = z0 + size / 2;
                    FillRect(ptr, stride, width, height, cx - 2, cz - 2, 4, Color.FromRgb(0xFF, 0xFF, 0xFF));
                }
            }

            // 矿石密度（热力图风格）
            if (vm.ShowOres && chunk.OreCounts.Count > 0)
            {
                var totalOres = chunk.OreCounts.Values.Sum();
                if (totalOres > 0)
                {
                    // 根据总矿石数决定亮度
                    var intensity = Math.Min(255, 50 + totalOres * 8);
                    var oreColor = Color.FromRgb((byte)intensity, (byte)(intensity / 2), 0);
                    // 右下角小方块表示有矿
                    FillRect(ptr, stride, width, height, x0 + size - 4, z0 + size - 4, 4, oreColor);
                }
            }
        }
    }

    private void DrawGrid(WriteableBitmap.Lock buf, int width, int height, double centerX, double centerZ, int scale)
    {
        unsafe
        {
            var ptr = (byte*)buf.Address;
            var stride = buf.RowBytes;
            var gridColor = Color.FromRgb(0x30, 0x36, 0x3D); // 深灰网格

            // 竖线
            var startX = (int)Math.Floor((-centerX) / (double)scale) * scale + centerX;
            for (double x = startX; x < width; x += scale)
            {
                var xi = (int)Math.Round(x);
                if (xi >= 0 && xi < width)
                {
                    for (int y = 0; y < height; y++)
                    {
                        var idx = y * stride + xi * 4;
                        ptr[idx] = gridColor.B;
                        ptr[idx + 1] = gridColor.G;
                        ptr[idx + 2] = gridColor.R;
                        ptr[idx + 3] = 80; // 半透明
                    }
                }
            }

            // 横线
            var startZ = (int)Math.Floor((-centerZ) / (double)scale) * scale + centerZ;
            for (double z = startZ; z < height; z += scale)
            {
                var zi = (int)Math.Round(z);
                if (zi >= 0 && zi < height)
                {
                    var rowStart = zi * stride;
                    for (int x = 0; x < width; x++)
                    {
                        var idx = rowStart + x * 4;
                        ptr[idx] = gridColor.B;
                        ptr[idx + 1] = gridColor.G;
                        ptr[idx + 2] = gridColor.R;
                        ptr[idx + 3] = 80;
                    }
                }
            }
        }
    }

    private void DrawOrigin(WriteableBitmap.Lock buf, double centerX, double centerZ, int scale)
    {
        unsafe
        {
            var ptr = (byte*)buf.Address;
            var stride = buf.RowBytes;
            var width = buf.Size.Width;
            var height = buf.Size.Height;

            var ox = (int)Math.Round(centerX);
            var oz = (int)Math.Round(centerZ);

            if (ox >= 0 && ox < width && oz >= 0 && oz < height)
            {
                // 绘制十字准星
                var color = Color.FromRgb(0x7F, 0xE0, 0x8C);
                for (int i = -8; i <= 8; i++)
                {
                    // 横线
                    if (ox + i >= 0 && ox + i < width)
                    {
                        var idx = oz * stride + (ox + i) * 4;
                        ptr[idx] = color.B; ptr[idx+1] = color.G; ptr[idx+2] = color.R; ptr[idx+3] = 255;
                    }
                    // 竖线
                    if (oz + i >= 0 && oz + i < height)
                    {
                        var idx = (oz + i) * stride + ox * 4;
                        ptr[idx] = color.B; ptr[idx+1] = color.G; ptr[idx+2] = color.R; ptr[idx+3] = 255;
                    }
                }
            }
        }
    }

    private void FillRect(byte* ptr, int stride, int width, int height, int x, int z, int size, Color color)
    {
        if (x >= width || z >= height || x + size <= 0 || z + size <= 0) return;
        
        int x0 = Math.Max(0, x);
        int z0 = Math.Max(0, z);
        int x1 = Math.Min(width, x + size);
        int z1 = Math.Min(height, z + size);

        for (int row = z0; row < z1; row++)
        {
            var rowPtr = ptr + row * stride + x0 * 4;
            for (int col = x0; col < x1; col++)
            {
                rowPtr[(col - x0) * 4 + 0] = color.B;
                rowPtr[(col - x0) * 4 + 1] = color.G;
                rowPtr[(col - x0) * 4 + 2] = color.R;
                rowPtr[(col - x0) * 4 + 3] = color.A;
            }
        }
    }

    private void DrawRect(byte* ptr, int stride, int width, int height, int x, int z, int size, Color color, int thickness)
    {
        // 上边
        FillRect(ptr, stride, width, height, x, z, size, color);
        // 下边
        FillRect(ptr, stride, width, height, x, z + size - thickness, size, color);
        // 左边
        FillRect(ptr, stride, width, height, x, z, thickness, size);
        // 右边
        FillRect(ptr, stride, width, height, x + size - thickness, z, thickness, size);
    }

    private Color GetBiomeColor(int biomeId)
    {
        return biomeId switch
        {
            1 => Color.FromRgb(0x8F, 0xBC, 0x8F), // 平原 - 浅绿
            4 => Color.FromRgb(0x22, 0x8B, 0x22), // 森林 - 深绿
            5 => Color.FromRgb(0x3C, 0x6E, 0x3C), // 针叶林
            6 => Color.FromRgb(0x55, 0x6B, 0x2F), // 沼泽 - 橄榄
            2 => Color.FromRgb(0xDE, 0xB8, 0x87), // 沙漠 - 浅棕
            3 => Color.FromRgb(0x8B, 0x73, 0x55), // 极端山地 - 灰褐
            14 => Color.FromRgb(0xFF, 0x00, 0xFF), // 蘑菇岛 - 品红
            16 => Color.FromRgb(0xF4, 0xA4, 0x60), // 海滩 - 沙色
            21 => Color.FromRgb(0x00, 0x64, 0x00), // 丛林 - 深绿
            35 => Color.FromRgb(0xF0, 0xE6, 0x8C), // 稀树草原 - 卡其
            39 => Color.FromRgb(0xCD, 0x5C, 0x5C), // 恶地 - 红褐
            0 => Color.FromRgb(0x1E, 0x90, 0xFF), // 海洋 - 蓝
            24 => Color.FromRgb(0x00, 0x00, 0x8B), // 深海 - 深蓝
            10 => Color.FromRgb(0xB0, 0xC4, 0xDE), // 冰冻海洋 - 浅蓝灰
            11 => Color.FromRgb(0x87, 0xCE, 0xEB), // 冰冻河流
            12 => Color.FromRgb(0xFF, 0xFF, 0xFF), // 雪原 - 白
            30 => Color.FromRgb(0xE0, 0xFF, 0xFF), // 雪地针叶林
            _ => Color.FromRgb(0x66, 0x66, 0x66), // 未知 - 灰
        };
    }
}