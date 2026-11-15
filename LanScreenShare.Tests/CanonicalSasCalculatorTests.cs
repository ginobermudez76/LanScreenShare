using System;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LanScreenShare.Core.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public partial class CanonicalSasCalculatorTests
{
    [GeneratedRegex(@"^\d{3}-\d{3}$")]
    private static partial Regex SasFormatRegex();

    private byte[] _dummyHostCert = null!;
    private byte[] _dummyViewerCert = null!;
    private byte[] _dummyHostNonce = null!;
    private byte[] _dummyViewerNonce = null!;

    [TestInitialize]
    public void Setup()
    {
        _dummyHostCert = new byte[] { 0x30, 0x82, 0x01, 0x0A, 0x01, 0x02, 0x03, 0x04 };
        _dummyViewerCert = new byte[] { 0x30, 0x82, 0x01, 0x14, 0x05, 0x06, 0x07, 0x08 };
        _dummyHostNonce = new byte[16];
        _dummyViewerNonce = new byte[16];
        RandomNumberGenerator.Fill(_dummyHostNonce);
        RandomNumberGenerator.Fill(_dummyViewerNonce);
    }

    [TestMethod]
    public void ComputeSas_DeterministicResult()
    {
        var creds1 = new PairingCredentials(_dummyHostCert, _dummyViewerCert, _dummyHostNonce, _dummyViewerNonce);
        var (code1, str1) = CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds1);

        var creds2 = new PairingCredentials(_dummyHostCert, _dummyViewerCert, _dummyHostNonce, _dummyViewerNonce);
        var (code2, str2) = CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds2);

        Assert.AreEqual(code1, code2);
        Assert.AreEqual(str1, str2);
        Assert.IsTrue(code1 < 1_000_000);
        Assert.IsTrue(SasFormatRegex().IsMatch(str1));
    }

    [TestMethod]
    public void ComputeSas_DifferentNonces_ProduceDifferentCodes()
    {
        var creds1 = new PairingCredentials(_dummyHostCert, _dummyViewerCert, _dummyHostNonce, _dummyViewerNonce);
        var (code1, _) = CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds1);

        byte[] modifiedViewerNonce = (byte[])_dummyViewerNonce.Clone();
        modifiedViewerNonce[0] ^= 0xFF;

        var creds2 = new PairingCredentials(_dummyHostCert, _dummyViewerCert, _dummyHostNonce, modifiedViewerNonce);
        var (code2, _) = CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds2);

        // Very high probability they are different
        Assert.AreNotEqual(code1, code2);
    }

    [TestMethod]
    public void ComputeSas_DifferentCerts_ProduceDifferentCodes()
    {
        var creds1 = new PairingCredentials(_dummyHostCert, _dummyViewerCert, _dummyHostNonce, _dummyViewerNonce);
        var (code1, _) = CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds1);

        byte[] modifiedHostCert = (byte[])_dummyHostCert.Clone();
        modifiedHostCert[0] = 0x31;

        var creds2 = new PairingCredentials(modifiedHostCert, _dummyViewerCert, _dummyHostNonce, _dummyViewerNonce);
        var (code2, _) = CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds2);

        Assert.AreNotEqual(code1, code2);
    }

    [TestMethod]
    public void ComputeSas_InvalidHostNonceLength_ThrowsArgumentException()
    {
        byte[] invalidNonce = new byte[15];
        Assert.ThrowsException<ArgumentException>(() =>
        {
            var creds = new PairingCredentials(_dummyHostCert, _dummyViewerCert, invalidNonce, _dummyViewerNonce);
            CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds);
        });
    }

    [TestMethod]
    public void ComputeSas_EmptyCertificate_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
        {
            var creds = new PairingCredentials(Array.Empty<byte>(), _dummyViewerCert, _dummyHostNonce, _dummyViewerNonce);
            CanonicalSasCalculator.ComputeSas(CanonicalSasCalculator.CurrentProtocolVersion, in creds);
        });
    }
}
