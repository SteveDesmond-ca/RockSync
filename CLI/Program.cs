using System.IO.Abstractions;
using ecoAPM.RockSync.Core;
using Renci.SshNet;

var fs = new FileSystem();

const string username = "u0_a262";
var credentials = new PasswordAuthenticationMethod(username, "testPass");
var connection = new ConnectionInfo("10.0.0.200", 8022, username, credentials);
var sftp = new SftpClient(connection);

var synchronizer = new Synchronizer(fs, sftp, Console.WriteLine);
await synchronizer.Synchronize(@"D:\Music\", "/storage/emulated/0/Music/", CancellationToken.None);