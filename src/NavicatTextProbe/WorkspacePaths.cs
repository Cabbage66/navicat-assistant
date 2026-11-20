using System.IO;

namespace NavicatTextProbe;

internal static class WorkspacePaths
{
    public static string FindAssistantRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "NavicatAssistant.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NavicatAssistant");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
