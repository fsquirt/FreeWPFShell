using System.IO;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FreeWPFShell.Tests.Integration
{

    [TestClass]
    public class SftpIntegrationTests
    {
        private readonly SshTestConfig? _cfg;
        private readonly SftpClient? _sftp;
        private readonly string _sandbox = "";
        private readonly string _localSandbox;

        public SftpIntegrationTests()
        {
            _cfg = SshTestConfig.Load();
            if (_cfg != null)
            {
                _sftp = new SftpClient(_cfg.Host, _cfg.Port, _cfg.User, _cfg.Password);
                _sftp.Connect();
                _sandbox = $"/tmp/fwpt_sftp_test_{Guid.NewGuid():N}";
                _sftp.CreateDirectory(_sandbox);
            }
            _localSandbox = Path.Combine(Path.GetTempPath(), "fwpt_sftp_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_localSandbox);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (_sftp != null)
            {
                try { if (_sftp.IsConnected && _sandbox.Length > 0) CleanupRemote(_sandbox); } catch { }
                _sftp.Dispose();
            }
            try { if (Directory.Exists(_localSandbox)) Directory.Delete(_localSandbox, true); } catch { }
        }

        private void SkipIfNoConfig()
        {
            if (_cfg == null || _sftp == null || _sandbox.Length == 0)
                Assert.Inconclusive("未配置 sshtest.json，跳过 SFTP 集成测试。");
        }

        private string Remote(string name) => $"{_sandbox}/{name}";


        private void CleanupRemote(string path)
        {
            foreach (var f in _sftp!.ListDirectory(path))
            {
                if (f.Name == "." || f.Name == "..") continue;
                if (f.IsDirectory) CleanupRemote(f.FullName);
                else _sftp.DeleteFile(f.FullName);
            }
            _sftp.DeleteDirectory(path);
        }


        private void CreateRemoteDir(string path)
        {
            var parts = path.Trim('/').Split('/');
            string cur = "";
            foreach (var p in parts)
            {
                if (p.Length == 0) continue;
                cur = cur.Length == 0 ? "/" + p : cur + "/" + p;
                if (!_sftp!.Exists(cur)) _sftp.CreateDirectory(cur);
            }
        }



        [TestMethod]
        public void Create_FileAndDirectory()
        {
            SkipIfNoConfig();
            string dir = Remote("newdir");
            string file = Remote("newfile.txt");

            _sftp!.CreateDirectory(dir);
            Assert.IsTrue(_sftp.Exists(dir), "创建的文件夹应存在");

            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
            _sftp.UploadFile(ms, file, true);
            Assert.IsTrue(_sftp.Exists(file), "创建的文件应存在");
        }

        [TestMethod]
        public void Create_NestedDirectory_WithMultipleLevels()
        {
            SkipIfNoConfig();
            string nested = $"{_sandbox}/a/b/c/d";
            CreateRemoteDir(nested);
            Assert.IsTrue(_sftp!.Exists(nested), "多级目录应创建成功");
        }



        [TestMethod]
        public void Delete_File()
        {
            SkipIfNoConfig();
            string file = Remote("del_file.txt");
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("x")))
                _sftp!.UploadFile(ms, file, true);

            _sftp.DeleteFile(file);
            Assert.IsFalse(_sftp.Exists(file), "删除后文件应不存在");
        }

        [TestMethod]
        public void Delete_Directory_Recursive_WithContents()
        {
            SkipIfNoConfig();
            string dir = Remote("del_dir");
            CreateRemoteDir(dir);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("content")))
                _sftp!.UploadFile(ms, $"{dir}/inner.txt", true);

            CleanupRemote(dir); 
            Assert.IsFalse(_sftp.Exists(dir), "递归删除后目录应不存在");
        }



        [TestMethod]
        public void Rename_File()
        {
            SkipIfNoConfig();
            string src = Remote("old_name.txt");
            string dst = Remote("new_name.txt");
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("rename me")))
                _sftp!.UploadFile(ms, src, true);

            _sftp.RenameFile(src, dst);
            Assert.IsFalse(_sftp.Exists(src), "重命名后旧文件不应存在");
            Assert.IsTrue(_sftp.Exists(dst), "重命名后新文件应存在");
        }

        [TestMethod]
        public void Rename_Directory()
        {
            SkipIfNoConfig();
            string src = Remote("old_dir");
            string dst = Remote("new_dir");
            _sftp!.CreateDirectory(src);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("inner")))
                _sftp.UploadFile(ms, $"{src}/f.txt", true);

            _sftp.RenameFile(src, dst);
            Assert.IsFalse(_sftp.Exists(src), "重命名后旧目录不应存在");
            Assert.IsTrue(_sftp.Exists(dst), "重命名后新目录应存在");
            Assert.IsTrue(_sftp.Exists($"{dst}/f.txt"), "重命名后目录内容应保留");
        }



        [TestMethod]
        public void Upload_File_ContentVerified()
        {
            SkipIfNoConfig();
            string content = "upload content " + Guid.NewGuid();
            string localFile = Path.Combine(_localSandbox, "up.txt");
            File.WriteAllText(localFile, content);

            string remoteFile = Remote("up.txt");
            using (var fs = File.OpenRead(localFile))
                _sftp!.UploadFile(fs, remoteFile, true);

            using var download = new MemoryStream();
            _sftp.DownloadFile(remoteFile, download);
            Assert.AreEqual(content, Encoding.UTF8.GetString(download.ToArray()), "上传内容应一致");
        }

        [TestMethod]
        public void Upload_Directory_Recursive()
        {
            SkipIfNoConfig();
            string localRoot = Path.Combine(_localSandbox, "upload_dir");
            Directory.CreateDirectory(Path.Combine(localRoot, "sub", "deep"));
            File.WriteAllText(Path.Combine(localRoot, "root.txt"), "root");
            File.WriteAllText(Path.Combine(localRoot, "sub", "sub.txt"), "sub");
            File.WriteAllText(Path.Combine(localRoot, "sub", "deep", "deep.txt"), "deep");

            string remoteRoot = Remote("upload_dir");
            CopyLocalDirRecursive(localRoot, remoteRoot);

            Assert.IsTrue(_sftp!.Exists($"{remoteRoot}/root.txt"), "根文件应上传");
            Assert.IsTrue(_sftp.Exists($"{remoteRoot}/sub/sub.txt"), "子目录文件应上传");
            Assert.IsTrue(_sftp.Exists($"{remoteRoot}/sub/deep/deep.txt"), "深层文件应上传");
        }

        private void CopyLocalDirRecursive(string localDir, string remoteDir)
        {
            CreateRemoteDir(remoteDir);
            foreach (var file in Directory.GetFiles(localDir))
            {
                using var fs = File.OpenRead(file);
                _sftp!.UploadFile(fs, $"{remoteDir}/{Path.GetFileName(file)}", true);
            }
            foreach (var sub in Directory.GetDirectories(localDir))
            {
                CopyLocalDirRecursive(sub, $"{remoteDir}/{Path.GetFileName(sub)}");
            }
        }



        [TestMethod]
        public void Download_File_ContentVerified()
        {
            SkipIfNoConfig();
            string content = "download content " + Guid.NewGuid();
            string remoteFile = Remote("dl.txt");
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                _sftp!.UploadFile(ms, remoteFile, true);

            string localFile = Path.Combine(_localSandbox, "dl.txt");
            using (var fs = File.Create(localFile))
                _sftp.DownloadFile(remoteFile, fs);

            Assert.AreEqual(content, File.ReadAllText(localFile), "下载内容应一致");
        }

        [TestMethod]
        public void Download_Directory_Recursive()
        {
            SkipIfNoConfig();
            string remoteRoot = Remote("dl_dir");
            CreateRemoteDir($"{remoteRoot}/sub/deep");
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("root")))
                _sftp!.UploadFile(ms, $"{remoteRoot}/root.txt", true);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("sub")))
                _sftp.UploadFile(ms, $"{remoteRoot}/sub/sub.txt", true);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("deep")))
                _sftp.UploadFile(ms, $"{remoteRoot}/sub/deep/deep.txt", true);

            string localRoot = Path.Combine(_localSandbox, "dl_dir");
            CopyRemoteDirRecursive(remoteRoot, localRoot);

            Assert.IsTrue(File.Exists(Path.Combine(localRoot, "root.txt")), "根文件应下载");
            Assert.IsTrue(File.Exists(Path.Combine(localRoot, "sub", "sub.txt")), "子目录文件应下载");
            Assert.IsTrue(File.Exists(Path.Combine(localRoot, "sub", "deep", "deep.txt")), "深层文件应下载");
            Assert.AreEqual("deep", File.ReadAllText(Path.Combine(localRoot, "sub", "deep", "deep.txt")));
        }

        private void CopyRemoteDirRecursive(string remoteDir, string localDir)
        {
            Directory.CreateDirectory(localDir);
            foreach (var f in _sftp!.ListDirectory(remoteDir))
            {
                if (f.Name == "." || f.Name == "..") continue;
                if (f.IsDirectory) CopyRemoteDirRecursive(f.FullName, Path.Combine(localDir, f.Name));
                else
                {
                    using var fs = File.Create(Path.Combine(localDir, f.Name));
                    _sftp.DownloadFile(f.FullName, fs);
                }
            }
        }



        [TestMethod]
        public void Upload_ReportsProgress()
        {
            SkipIfNoConfig();
            string localFile = Path.Combine(_localSandbox, "big.bin");
            var data = new byte[1024 * 256];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(localFile, data);

            string remoteFile = Remote("big.bin");
            var progressPoints = new List<ulong>();
            using (var fs = File.OpenRead(localFile))
            {
                _sftp!.UploadFile(fs, remoteFile, p => progressPoints.Add(p));
            }

            Assert.IsTrue(progressPoints.Count > 0, "应触发进度回调");

            Assert.IsTrue(progressPoints.All(p => p <= (ulong)data.Length), "进度不应超过文件大小");
            long remoteSize = _sftp!.GetAttributes(remoteFile).Size;
            Assert.AreEqual(data.LongLength, remoteSize, "上传后远程文件大小应等于源文件");
        }

        [TestMethod]
        public void Download_ReportsProgress()
        {
            SkipIfNoConfig();
            string remoteFile = Remote("big_dl.bin");
            var data = new byte[1024 * 256];
            new Random(7).NextBytes(data);
            using (var ms = new MemoryStream(data))
                _sftp!.UploadFile(ms, remoteFile, true);

            var progressPoints = new List<ulong>();
            string localFile = Path.Combine(_localSandbox, "big_dl.bin");
            using (var fs = File.Create(localFile))
            {
                _sftp.DownloadFile(remoteFile, fs, p => progressPoints.Add(p));
            }

            Assert.IsTrue(progressPoints.Count > 0, "应触发进度回调");
            Assert.IsTrue(progressPoints.All(p => p <= (ulong)data.Length), "进度不应超过文件大小");

            long localSize = new FileInfo(localFile).Length;
            Assert.AreEqual(data.LongLength, localSize, "下载后本地文件大小应等于远程文件");
        }



        [TestMethod]
        public void EditRoundTrip_DownloadModifyUpload()
        {
            SkipIfNoConfig();
            string remoteFile = Remote("edit.txt");
            string original = "line1\nline2\n";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(original)))
                _sftp!.UploadFile(ms, remoteFile, true);


            string localFile = Path.Combine(_localSandbox, "edit.txt");
            using (var fs = File.Create(localFile))
                _sftp.DownloadFile(remoteFile, fs);


            File.AppendAllText(localFile, "line3\n");


            using (var fs = File.OpenRead(localFile))
                _sftp.UploadFile(fs, remoteFile, true);


            using var download = new MemoryStream();
            _sftp.DownloadFile(remoteFile, download);
            string updated = Encoding.UTF8.GetString(download.ToArray());
            Assert.IsTrue(updated.Contains("line3"), "编辑后内容应回传到远程");
            Assert.IsTrue(updated.StartsWith("line1"), "原内容应保留");
        }



        [TestMethod]
        public async Task Cancel_Upload_ByDisconnecting_InterruptsTransfer()
        {
            SkipIfNoConfig();
            string localFile = Path.Combine(_localSandbox, "cancel_upload.bin");

            var data = new byte[64 * 1024 * 1024];
            new Random(99).NextBytes(data);
            File.WriteAllBytes(localFile, data);

            string remoteFile = Remote("cancel_upload.bin");
            bool interrupted = false;


            using var sftp = new SftpClient(_cfg!.Host, _cfg.Port, _cfg.User, _cfg.Password);
            sftp.Connect();

            using (var fs = File.OpenRead(localFile))
            {
                var uploadTask = Task.Run(() =>
                {
                    try { sftp.UploadFile(fs, remoteFile, null); }
                    catch (Exception) { }
                });

                await Task.Delay(200); 

                try { sftp.Disconnect(); } catch { }

                await uploadTask;
                interrupted = true; 
            }

            Assert.IsTrue(interrupted, "断开连接后上传应被中断");
        }

        [TestMethod]
        public void Cancel_Download_ByDisconnecting_InterruptsTransfer()
        {
            SkipIfNoConfig();
            string remoteFile = Remote("cancel_dl.bin");
            var data = new byte[64 * 1024 * 1024];
            new Random(5).NextBytes(data);


            using (var prep = new SftpClient(_cfg!.Host, _cfg.Port, _cfg.User, _cfg.Password))
            {
                prep.Connect();
                using var ms = new MemoryStream(data);
                prep.UploadFile(ms, remoteFile, true);
            }

            string localFile = Path.Combine(_localSandbox, "cancel_dl.bin");
            bool interrupted = false;


            using (var sftp = new SftpClient(_cfg.Host, _cfg.Port, _cfg.User, _cfg.Password))
            {
                sftp.Connect();
                using (var fs = File.Create(localFile))
                {
                    var dlTask = Task.Run(() =>
                    {
                        try { sftp.DownloadFile(remoteFile, fs); }
                        catch (Exception) { }
                    });

                    Thread.Sleep(200);
                    try { sftp.Disconnect(); } catch { }
                    dlTask.Wait();
                }
                interrupted = true;
            }

            Assert.IsTrue(interrupted, "断开连接后下载应被中断");
        }
    }
}
