using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using NovaLauncher.Models;
using NovaLauncher.PluginApi;
using NovaLauncher.PluginHosting;

namespace NovaLauncher.Services;

public sealed class PluginInfoModel
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string File { get; init; } = "";
    public string Lang { get; init; } = "C#";
    public bool Loaded { get; init; }
    public string StatusText => Loaded ? "已加载" : "加载失败";
}

/// <summary>
/// 插件系统：扫描 plugins 目录，按扩展名加载三种语言的插件。
///   *.dll      -> .NET 程序集（ILauncherPlugin）；非托管程序集回退为 C 插件
///   *.py       -> Python 插件（python3 进程桥）
///   *.dylib/so -> C/C++ 原生插件（nova_plugin.h ABI）
/// </summary>
public static class PluginService
{
    public static ObservableCollection<PluginInfoModel> Plugins { get; } = new();
    public static ObservableCollection<HomeCardInfo> RegisteredHomeCards { get; } = new();
    public static ObservableCollection<PluginPageInfo> RegisteredPages { get; } = new();
    private static readonly List<IDisposable> _loaded = new();
    private static readonly List<IDisposable> _hosts = new();
    private static readonly Dictionary<string, ILauncherPlugin> _pageOwners = new();
    private static readonly Dictionary<string, ILauncherPlugin> _cardOwners = new();

    /// <summary>按页面 Key 回调持有该页面的插件的 OnAction（插件页面按钮点击入口）。</summary>
    public static void InvokeAction(string pageKey, string actionId)
    {
        if (_pageOwners.TryGetValue(pageKey, out var plugin))
        {
            try { plugin.OnAction(actionId); }
            catch (Exception e) { LogService.Warn($"插件动作执行失败 {actionId}: {e.Message}"); }
        }
    }

    /// <summary>更新插件页面正文（供插件在后台任务中刷新进度）。</summary>
    public static void UpdatePageBody(string pageKey, string body)
    {
        var page = RegisteredPages.FirstOrDefault(p => p.Key == pageKey);
        if (page != null) page.SetBody(body);
    }

    /// <summary>更新主页卡片正文。</summary>
    public static void UpdateCardBody(string cardId, string body)
    {
        var c = RegisteredHomeCards.FirstOrDefault(p => p.Id == cardId);
        if (c != null) c.Body = body;
    }

    public static void LoadPlugins()
    {
        Plugins.Clear();
        RegisteredHomeCards.Clear();
        RegisteredPages.Clear();
        _pageOwners.Clear();
        _cardOwners.Clear();
        foreach (var d in _loaded) { try { d.Dispose(); } catch { } }
        _loaded.Clear();
        foreach (var h in _hosts) { try { h.Dispose(); } catch { } }
        _hosts.Clear();

        if (!Directory.Exists(AppPaths.Plugins)) return;
        foreach (var file in Directory.GetFiles(AppPaths.Plugins)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("NovaLauncher") || name.StartsWith("Avalonia") || name.StartsWith("CommunityToolkit")) continue;

            try
            {
                switch (Path.GetExtension(name).ToLowerInvariant())
                {
                    case ".py": LoadPythonPlugin(file); break;
                    case ".dylib":
                    case ".so": LoadCPlugin(file); break;
                    case ".dll": LoadNetPlugin(file); break;
                }
            }
            catch (Exception e)
            {
                LogService.Warn($"插件加载失败 {name}: {e.Message}");
                Plugins.Add(new PluginInfoModel { Name = Path.GetFileNameWithoutExtension(name), File = name, Loaded = false });
            }
        }
        if (Plugins.Count == 0)
            LogService.Info("暂无插件。将插件 dll / py / dylib 放入 plugins 目录后重启生效（也支持自制插件）。");
    }

    // ---------- .NET 程序集插件 ----------

    private static void LoadNetPlugin(string dll)
    {
        try
        {
            var name = Path.GetFileName(dll);
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(ILauncherPlugin).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                var plugin = (ILauncherPlugin)Activator.CreateInstance(type)!;
                plugin.OnLoad(new LauncherApiImpl(plugin.Name, plugin));
                Plugins.Add(new PluginInfoModel
                {
                    Name = plugin.Name,
                    Version = plugin.Version,
                    Author = plugin.Author,
                    Description = plugin.Description,
                    File = name,
                    Lang = "C#",
                    Loaded = true,
                });
                LogService.Info($"插件已加载: {plugin.Name} v{plugin.Version} by {plugin.Author}");
            }
        }
        catch (BadImageFormatException)
        {
            // 非托管 .dll（Windows 上的 C 插件）
            LoadCPlugin(dll);
        }
    }

    // ---------- Python 插件 ----------

    private static void LoadPythonPlugin(string file)
    {
        var host = new PythonPluginHost(file);
        var adapter = MakeAdapter(host, "Python");
        var api = new LauncherApiImpl(Path.GetFileNameWithoutExtension(file), adapter);
        if (!host.Start(api))
        {
            Plugins.Add(new PluginInfoModel
            {
                Name = adapter.Name,
                File = Path.GetFileName(file),
                Lang = "Python",
                Loaded = false,
            });
            LogService.Warn($"Python 插件加载失败 {Path.GetFileName(file)}: {host.StartupError}");
            host.Dispose();
            return;
        }
        _hosts.Add(host);
        Plugins.Add(new PluginInfoModel
        {
            Name = host.Name,
            Version = host.Version,
            Author = host.Author,
            Description = host.Description,
            File = Path.GetFileName(file),
            Lang = "Python",
            Loaded = true,
        });
        LogService.Info($"Python 插件已加载: {host.Name} v{host.Version} by {host.Author}");
    }

    // ---------- C 原生插件 ----------

    private static void LoadCPlugin(string file)
    {
        var host = new CPluginHost(file);
        var adapter = MakeAdapter(host, "C");
        var api = new LauncherApiImpl(Path.GetFileNameWithoutExtension(file), adapter);
        if (!host.Load(api))
        {
            Plugins.Add(new PluginInfoModel
            {
                Name = adapter.Name,
                File = Path.GetFileName(file),
                Lang = "C",
                Loaded = false,
            });
            LogService.Warn($"C 插件加载失败 {Path.GetFileName(file)}: {host.StartupError}");
            host.Dispose();
            return;
        }
        _hosts.Add(host);
        Plugins.Add(new PluginInfoModel
        {
            Name = host.Name,
            Version = host.Version,
            Author = host.Author,
            Description = host.Description,
            File = Path.GetFileName(file),
            Lang = "C",
            Loaded = true,
        });
        LogService.Info($"C 插件已加载: {host.Name} v{host.Version} by {host.Author}");
    }

    /// <summary>把 Python/C 宿主包装成 ILauncherPlugin，统一页面按钮与卸载回调。</summary>
    private static ILauncherPlugin MakeAdapter(object host, string lang)
    {
        return new HostedPluginAdapter(host, lang);
    }

    private sealed class HostedPluginAdapter : ILauncherPlugin
    {
        private readonly object _host;
        private readonly string _lang;
        public HostedPluginAdapter(object host, string lang) { _host = host; _lang = lang; }

        private string HostName => _host switch
        {
            PythonPluginHost p => p.Name,
            CPluginHost c => c.Name,
            _ => "",
        };

        public string Name => HostName == "" ? "?" : HostName;
        public string Version => _host switch
        {
            PythonPluginHost p => p.Version,
            CPluginHost c => c.Version,
            _ => "",
        };
        public string Author => _host switch
        {
            PythonPluginHost p => p.Author,
            CPluginHost c => c.Author,
            _ => "",
        };
        public string Description => _host switch
        {
            PythonPluginHost p => p.Description,
            CPluginHost c => c.Description,
            _ => "",
        };

        public void OnLoad(ILauncherApi api) { } // 加载已由宿主完成

        public void OnUnload()
        {
            switch (_host)
            {
                case PythonPluginHost p: p.Unload(); break;
                case CPluginHost c: c.Unload(); break;
            }
        }

        public void OnAction(string actionId)
        {
            string? err = _host switch
            {
                PythonPluginHost p => p.InvokeAction(actionId),
                CPluginHost c => c.InvokeAction(actionId),
                _ => "未知宿主",
            };
            if (err != null) LogService.Warn($"[插件:{HostName}] {err}");
        }
    }

    private sealed class LauncherApiImpl : ILauncherApi
    {
        private string _plugin;
        private readonly ILauncherPlugin _owner;
        public LauncherApiImpl(string plugin, ILauncherPlugin owner) { _plugin = plugin; _owner = owner; }

        public void SetPluginName(string name) { _plugin = name; }

        public void Log(string message) => LogService.Info($"[插件:{_plugin}] {message}");
        public void Notify(string title, string message) => LogService.Info($"[通知:{_plugin}] {title}: {message}");
        public string GetSetting(string key, string defaultValue = "") => SettingsService.S.PluginSettings.TryGetValue(_plugin + "." + key, out var v) ? v : defaultValue;
        public void SetSetting(string key, string value)
        {
            SettingsService.S.PluginSettings[_plugin + "." + key] = value;
            SettingsService.Save();
        }
        public string LauncherHome => AppPaths.Home;
        public string GameRoot => AppPaths.Games;
        public void RegisterHomeCard(HomeCardInfo card)
        {
            if (string.IsNullOrWhiteSpace(card.Title) || string.IsNullOrWhiteSpace(card.Id)) return;
            RegisteredHomeCards.Add(card);
            _cardOwners[card.Id] = _owner;
            LogService.Info($"[插件:{_plugin}] 已注册主页卡片「{card.Title}」");
        }
        public void RegisterPage(PluginPageInfo page)
        {
            if (string.IsNullOrWhiteSpace(page.Key) || string.IsNullOrWhiteSpace(page.Title)) return;
            RegisteredPages.Add(page);
            _pageOwners[page.Key] = _owner;
            LogService.Info($"[插件:{_plugin}] 已注册插件页面「{page.Title}」");
        }
        public void UpdatePage(string key, string body)
        {
            var page = RegisteredPages.FirstOrDefault(p => p.Key == key);
            if (page != null) page.Body = body;
        }
        public void UpdateHomeCard(string cardId, string body)
        {
            var card = RegisteredHomeCards.FirstOrDefault(p => p.Id == cardId);
            if (card != null) card.Body = body;
        }
    }
}
