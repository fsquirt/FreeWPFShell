using System.Diagnostics;
using System.IO;
using System.Text;
using FreeWPFShell.Repositories;

namespace FreeWPFShell.Tests.Repositories
{
    /// <summary>
    /// SSH 密钥导入/导出功能测试。
    /// 使用 ssh-keygen 生成临时测试密钥，验证导入、查询、导出还原。
    /// KeyRepository 使用临时文件路径，不污染真实密钥库。
    /// </summary>
    [TestClass]
    public class KeyRepositoryTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _keysFile;
        private KeyRepository? _repo;

        public KeyRepositoryTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "fwpt_key_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _keysFile = Path.Combine(_tempDir, "test_keys.json");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [TestCleanup]
        public void Cleanup()
        {
            Dispose();
        }

        private KeyRepository CreateRepo()
        {
            _repo = new KeyRepository(_keysFile);
            return _repo;
        }

        /// <summary>用 ssh-keygen 生成一个密钥文件，返回路径。</summary>
        private string GenerateKey(string name, string? passphrase = null)
        {
            string keyPath = Path.Combine(_tempDir, name);
            var psi = new ProcessStartInfo
            {
                FileName = "C:\\Windows\\System32\\OpenSSH\\ssh-keygen.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("rsa");
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add("2048");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(keyPath);
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-N");
            psi.ArgumentList.Add(passphrase ?? "");

            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            Assert.IsTrue(File.Exists(keyPath), $"ssh-keygen 应生成密钥文件: {keyPath}");
            return keyPath;
        }

        [TestMethod]
        public void Import_NoPassphraseKey_AddsToRepo()
        {
            var repo = CreateRepo();
            string keyFile = GenerateKey("id_rsa_nopass");

            var key = repo.Import(keyFile, "test-nopass", null);

            Assert.IsNotNull(key);
            Assert.AreEqual("test-nopass", key.Name);
            Assert.IsFalse(key.HasPassphrase, "无密码密钥不应标记 HasPassphrase");
            Assert.IsTrue(repo.GetAll().Count == 1, "导入后应有 1 个密钥");
            Assert.IsNotNull(repo.FindById(key.Id), "应能按 Id 查到密钥");
        }

        [TestMethod]
        public void Import_Key_CanBeExportedBack()
        {
            var repo = CreateRepo();
            string keyFile = GenerateKey("id_rsa_export");
            string originalContent = File.ReadAllText(keyFile);

            var key = repo.Import(keyFile, "export-test", null);

            // 导出：从 PrivateKeyBase64 解码还原
            string exported = Encoding.UTF8.GetString(Convert.FromBase64String(key.PrivateKeyBase64));
            Assert.AreEqual(originalContent.Trim(), exported.Trim(), "导出内容应与原始密钥文件一致");
        }

        [TestMethod]
        public void Import_WithPassphrase_CanLoad()
        {
            var repo = CreateRepo();
            string keyFile = GenerateKey("id_rsa_pass", "secret123");

            var key = repo.Import(keyFile, "pass-test", "secret123");

            Assert.IsTrue(key.HasPassphrase, "带密码密钥应标记 HasPassphrase");
            // 应能用密码加载为 PrivateKeyFile
            var loaded = repo.LoadPrivateKeyFile(key.Id);
            Assert.IsNotNull(loaded);
        }

        [TestMethod]
        public void Delete_RemovesKey()
        {
            var repo = CreateRepo();
            string keyFile = GenerateKey("id_rsa_del");
            var key = repo.Import(keyFile, "del-test", null);

            repo.Delete(key.Id);

            Assert.IsNull(repo.FindById(key.Id), "删除后不应再查到密钥");
        }

        [TestMethod]
        public void GetAll_EmptyRepo_ReturnsEmpty()
        {
            var repo = CreateRepo();
            Assert.IsNotNull(repo.GetAll());
            Assert.AreEqual(0, repo.GetAll().Count);
        }
    }
}
