namespace Tedd.DotNetLab.Artifacts;

public static class OwnedDirectoryCleanup
{
    private static bool IsNonRootDirectory(string fullPath)
    {
        string trimmed=Path.TrimEndingDirectorySeparator(fullPath);
        string? pathRoot=Path.GetPathRoot(trimmed);
        return !string.IsNullOrEmpty(pathRoot)&&
            !string.Equals(trimmed,Path.TrimEndingDirectorySeparator(pathRoot),
                OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)&&
            Directory.GetParent(trimmed) is not null;
    }

    // Call only after owned processes and file operations have joined. Retries
    // accommodate delayed Windows handle release; they cannot establish quiescence.
    public static async Task DeleteAsync(string ownerRoot,string directory,CancellationToken cancellation=default)
    {
        if(!Path.IsPathFullyQualified(ownerRoot)||!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Cleanup requires fully qualified workspace and child paths.");
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownerRoot));
        string target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(!IsNonRootDirectory(root)||!target.StartsWith(root+Path.DirectorySeparatorChar,comparison))
            throw new InvalidOperationException("Cleanup requires a child of the owned workspace.");
        for(int attempt=0;;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            for(var ancestor=new DirectoryInfo(target);ancestor is not null;ancestor=ancestor.Parent)
                if(ancestor.Exists&&(ancestor.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidOperationException("Cleanup cannot traverse reparse points.");
            if(!Directory.Exists(target)) return;
            try { Directory.Delete(target,true); return; }
            catch(Exception error) when(attempt<49&&error is IOException or UnauthorizedAccessException)
            { await Task.Delay(100,cancellation); }
        }
    }
}
