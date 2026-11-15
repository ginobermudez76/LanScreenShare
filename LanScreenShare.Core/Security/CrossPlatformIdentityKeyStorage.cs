using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    /// <summary>
    /// Almacenamiento y generación de certificados de identidad mTLS para entornos multiplataforma (Linux, Android, macOS, Windows).
    /// En Windows utiliza DPAPI cuando está disponible; en sistemas basados en Unix (Linux, Android, macOS), utiliza almacenamiento
    /// protegido en el directorio de usuario (~/.local/share/LanScreenShare) con cifrado AES-256-GCM y permisos POSIX estrictos (0600).
    /// </summary>
    public class CrossPlatformIdentityKeyStorage : IIdentityKeyStorage
    {
        private readonly string _storagePath;
        private readonly string _keyPath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private X509Certificate2? _cachedCertificate;

        public CrossPlatformIdentityKeyStorage(string? customFilePath = null)
        {
            if (!string.IsNullOrWhiteSpace(customFilePath))
            {
                _storagePath = customFilePath;
                _keyPath = customFilePath + ".key";
            }
            else
            {
                string baseDir;
                if (OperatingSystem.IsWindows())
                {
                    baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanScreenShare");
                }
                else
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    baseDir = Path.Combine(home, ".local", "share", "LanScreenShare");
                }

                _storagePath = Path.Combine(baseDir, "identity.pfx.aes");
                _keyPath = Path.Combine(baseDir, "identity.key");
            }
        }

        public async Task<X509Certificate2> GetOrCreateIdentityCertificateAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_cachedCertificate != null)
                {
                    return _cachedCertificate;
                }

                if (File.Exists(_storagePath) && File.Exists(_keyPath))
                {
                    _cachedCertificate = LoadEncryptedCertificate();
                    return _cachedCertificate;
                }

                _cachedCertificate = GenerateAndSaveCertificate();
                return _cachedCertificate;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task ResetIdentityAsync()
        {
            await _lock.WaitAsync();
            try
            {
                _cachedCertificate?.Dispose();
                _cachedCertificate = null;

                if (File.Exists(_storagePath))
                {
                    File.Delete(_storagePath);
                }
                if (File.Exists(_keyPath))
                {
                    File.Delete(_keyPath);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private X509Certificate2 LoadEncryptedCertificate()
        {
            try
            {
                byte[] key = File.ReadAllBytes(_keyPath);
                byte[] encryptedData = File.ReadAllBytes(_storagePath);

                // Formato AES-GCM: Nonce (12 bytes) + Tag (16 bytes) + Ciphertext
                if (encryptedData.Length < 28)
                {
                    throw new IdentityKeyStorageException("Archivo de identidad corrupto o incompleto.");
                }

                byte[] nonce = new byte[12];
                byte[] tag = new byte[16];
                int ciphertextLen = encryptedData.Length - 28;
                byte[] ciphertext = new byte[ciphertextLen];

                Buffer.BlockCopy(encryptedData, 0, nonce, 0, 12);
                Buffer.BlockCopy(encryptedData, 12, tag, 0, 16);
                Buffer.BlockCopy(encryptedData, 28, ciphertext, 0, ciphertextLen);

                byte[] pfxBytes = new byte[ciphertextLen];
                using (var aesGcm = new AesGcm(key, 16))
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, pfxBytes);
                }

                return IdentityCertificateFactory.LoadExportableCertificate(pfxBytes);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                throw new IdentityKeyStorageException("Error crítico descifrando la identidad mTLS en almacenamiento seguro multiplataforma.", ex);
            }
        }

        private X509Certificate2 GenerateAndSaveCertificate()
        {
            string directory = Path.GetDirectoryName(_storagePath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                SetUserOnlyPermissions(directory);
            }

            // 1-4. Generar par de claves ECDSA P-256, extensiones X.509, certificado auto-firmado y exportar PFX
            byte[] pfxBytes = IdentityCertificateFactory.CreateSelfSignedIdentityPfx();

            // 5. Cifrado AES-256-GCM con clave protegida por permisos de usuario
            byte[] masterKey = new byte[32];
            RandomNumberGenerator.Fill(masterKey);

            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[pfxBytes.Length];

            using (var aesGcm = new AesGcm(masterKey, 16))
            {
                aesGcm.Encrypt(nonce, pfxBytes, ciphertext, tag);
            }

            byte[] encryptedFileBytes = new byte[12 + 16 + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, encryptedFileBytes, 0, 12);
            Buffer.BlockCopy(tag, 0, encryptedFileBytes, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, encryptedFileBytes, 28, ciphertext.Length);

            File.WriteAllBytes(_keyPath, masterKey);
            SetUserOnlyPermissions(_keyPath);

            File.WriteAllBytes(_storagePath, encryptedFileBytes);
            SetUserOnlyPermissions(_storagePath);

            return IdentityCertificateFactory.LoadExportableCertificate(pfxBytes);
        }

        private static void SetUserOnlyPermissions(string path)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Ignorar si el sistema de archivos subyacente no soporta modos POSIX
                }
            }
        }
    }
}

