using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

internal static class LocalizationTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Natsurainko.FluentLauncher"))) root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException("Launcher source root not found.");
        string project = Path.Combine(root.FullName, "Natsurainko.FluentLauncher");
        string assetsPath = Path.Combine(project, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            Console.WriteLine("SKIP XAML localization contract: restore the Windows launcher project first.");
            return;
        }
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var library = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(p => p.Name.StartsWith("Microsoft.WindowsAppSDK.WinUI/", StringComparison.OrdinalIgnoreCase));
        string packagePath = library.Value.GetProperty("path").GetString()!;
        string packageRoot = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(p => Path.Combine(p.Name, packagePath)).First(Directory.Exists);
        string projection = Directory.EnumerateFiles(Path.Combine(packageRoot, "lib"), "Microsoft.WinUI.dll", SearchOption.AllDirectories).First();
        using var stream = File.OpenRead(projection);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        string Name(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => DefinitionName((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => ReferenceName((TypeReferenceHandle)handle),
            _ => ""
        };
        string DefinitionName(TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        }
        string ReferenceName(TypeReferenceHandle handle)
        {
            var type = metadata.GetTypeReference(handle);
            return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        }
        var types = metadata.TypeDefinitions.Where(h => metadata.GetTypeDefinition(h).GetDeclaringType().IsNil)
            .ToDictionary(DefinitionName);
        bool HasProperty(string typeName, string property)
        {
            while (types.TryGetValue(typeName, out var handle))
            {
                var type = metadata.GetTypeDefinition(handle);
                if (type.GetProperties().Any(p => metadata.GetString(metadata.GetPropertyDefinition(p).Name) == property)) return true;
                typeName = Name(type.BaseType);
            }
            return false;
        }

        // Resolve properties against actual WinUI metadata without loading Windows UI in the headless runner.
        var xaml = XDocument.Load(Path.Combine(project, "Views", "Downloads", "ResourceDefaultPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var element in xaml.Descendants().Where(e => e.Attribute(x + "Uid") != null))
        {
            string uid = element.Attribute(x + "Uid")!.Value;
            int separator = uid.LastIndexOf('_');
            string csvPath = Path.Combine(root.FullName, "FluentLauncher.Infra.Localization", "Views",
                uid[..separator].Replace('_', Path.DirectorySeparatorChar) + ".csv");
            string id = uid[(separator + 1)..];
            var properties = File.ReadLines(csvPath).Skip(1).Select(line => line.Split(',', 3))
                .Where(row => row.Length >= 2 && row[0] == id).Select(row => row[1]).ToArray();
            check(properties.Length > 0 && properties.All(p => HasProperty("Microsoft.UI.Xaml.Controls." + element.Name.LocalName, p)),
                $"localized {uid} properties exist on {element.Name.LocalName}");
        }
        check(!HasProperty("Microsoft.UI.Xaml.Controls.HyperlinkButton", "Text")
            && HasProperty("Microsoft.UI.Xaml.Controls.HyperlinkButton", "Content"),
            "localization validation distinguishes TextBlock.Text from inherited button Content");
    }
}
