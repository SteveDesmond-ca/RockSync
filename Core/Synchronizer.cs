using System.Collections.Concurrent;
using System.IO.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace ecoAPM.RockSync.Core;

public class Synchronizer
{
    private static readonly string[] MediaFileTypes = ["mp3", "m4a", "wma", "flac"];

    private readonly IFileSystem _fs;
    private readonly ISftpClient _sftp;

    private readonly Action<string> _log;

    public Synchronizer(IFileSystem fs, ISftpClient sftp, Action<string> logger)
    {
        _fs = fs;
        _sftp = sftp;
        _log = logger;
    }

    public async Task Synchronize(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        _log("Connecting...");
        await _sftp.ConnectAsync(cancellationToken);
        await _sftp.ChangeDirectoryAsync(remotePath, cancellationToken);

        _log("Fetching files...");
        var remoteFetch = GetRemoteFiles(remotePath, cancellationToken);
        var local = GetLocalFiles(localPath);
        var remote = await remoteFetch;

        _log("Matching files...");
        var pairs = GetSyncPairs(local, remote, localPath, remotePath);
        var toUpload = pairs.Where(p => p.Value is null || p.Key.LastWriteTimeUtc > p.Value.LastWriteTimeUtc)
            .Select(p => p.Key).ToArray();
        var toDelete = remote.Where(r => !pairs.ContainsValue(r)).ToArray();

        await Parallel.ForEachAsync(toUpload, cancellationToken, async (file, c) => await Upload(file, localPath, remotePath, c));
        await Parallel.ForEachAsync(toDelete, cancellationToken, async (file, c) => await Delete(file, c));
        _log("Done!");
    }

    private async Task Upload(IFileInfo file, string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var src = file.FullName;
        var dest = src.Replace(localPath, remotePath).Replace('\\', '/');

        await EnsureParentExists(dest, remotePath, cancellationToken);

        _log($"Uploading [{src}] to [{dest}]...");
        await _sftp.UploadFileAsync(file.OpenRead(), dest, cancellationToken);
    }

    private async Task EnsureParentExists(string path, string root, CancellationToken cancellationToken)
    {
        if (path.Length <= root.Length)
            return;

        var parts = path.Split("/");
        var parent = string.Join('/', parts[..^1]);
        await EnsureParentExists(parent, root, cancellationToken);

        var exists = await _sftp.ExistsAsync(parent, cancellationToken);
        if (!exists)
        {
            _log($"Creating [{parent}]...");
            await _sftp.CreateDirectoryAsync(parent, cancellationToken);
        }
    }

    private async Task Delete(ISftpFile file, CancellationToken cancellationToken)
    {
        _log($"Deleting [{file.FullName}]...");
        await file.DeleteAsync(cancellationToken);
    }

    private IFileInfo[] GetLocalFiles(string directory)
        => _fs.DirectoryInfo.New(directory)
            .GetFiles("*.*", SearchOption.AllDirectories)
            .Where(IsMediaFile)
            .ToArray();

    private async Task<ISftpFile[]> GetRemoteFiles(string directory, CancellationToken cancellationToken)
    {
        var enumerator = _sftp.ListDirectoryAsync(directory, cancellationToken);

        var files = new ConcurrentBag<ISftpFile>();
        await Parallel.ForEachAsync(enumerator, cancellationToken, async (entry, c) =>
        {
            var toAdd = entry.IsDirectory ? await GetRemoteFiles(entry.FullName, c)
                : IsMediaFile(entry) ? [entry]
                : [];

            foreach (var file in toAdd)
                files.Add(file);
        });

        return files.ToArray();
    }

    private static bool IsMediaFile(IFileInfo file)
        => IsMediaFile(file.Name);

    private static bool IsMediaFile(ISftpFile file)
        => IsMediaFile(file.Name);

    private static bool IsMediaFile(string filename)
        => MediaFileTypes.Contains(filename.Split('.').Last());

    private static Dictionary<IFileInfo, ISftpFile?> GetSyncPairs(IFileInfo[] local, ISftpFile[] remote,
        string localPath, string remotePath)
        => local.ToDictionary(l => l,
            l => remote.FirstOrDefault(r =>
                r.FullName[remotePath.Length..] == l.FullName[localPath.Length..].Replace('\\', '/')));
}