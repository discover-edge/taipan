namespace Taipan.Tests;

public static class RepoPaths
{
    public static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Taipan.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Taipan.slnx not found above " + AppContext.BaseDirectory);
    }
}
