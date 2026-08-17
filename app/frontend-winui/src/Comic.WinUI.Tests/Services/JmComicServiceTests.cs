using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public class JmComicServiceTests
{
    [TestMethod]
    public void ParseMangaId_AcceptsAlbumQueryAndNumericInput()
    {
        Assert.AreEqual("12345", JmComicService.ParseMangaId("https://18comic.vip/album/12345").MangaId);
        Assert.AreEqual("678", JmComicService.ParseMangaId("https://18comic.vip/album?id=678").MangaId);
        Assert.AreEqual("42", JmComicService.ParseMangaId("42").MangaId);
        Assert.IsNull(JmComicService.ParseMangaId("https://example.test/no-id").MangaId);
    }

    [TestMethod]
    public void DecryptPayload_DecodesAesEcbEnvelope()
    {
        const long timestamp = 1_700_000_000;
        var plaintext = Encoding.UTF8.GetBytes("""{"id":123,"name":"测试漫画"}""");
        var keyText = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var result = JmComicService.DecryptPayload(timestamp, Convert.ToBase64String(encrypted));

        CollectionAssert.AreEqual(plaintext, result);
    }

    [TestMethod]
    public void CalculateBlockCount_MatchesJmRules()
    {
        Assert.AreEqual(0, JmComicService.CalculateBlockCount(220_980, 220_979, "001"));
        Assert.AreEqual(10, JmComicService.CalculateBlockCount(220_980, 220_980, "001"));
        Assert.IsTrue(JmComicService.CalculateBlockCount(220_980, 500_000, "001") is >= 2 and <= 16);
    }

    [TestMethod]
    public void ReorderVerticalBlocks_ReversesSourceBlockOrder()
    {
        var pixels = Enumerable.Range(0, 5)
            .SelectMany(row => Enumerable.Repeat((byte)row, 4))
            .ToArray();

        var result = JmComicService.ReorderVerticalBlocks(pixels, width: 1, height: 5, blockCount: 2);
        var firstByteOfEachRow = Enumerable.Range(0, 5).Select(row => result[row * 4]).ToArray();

        CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 0, 1 }, firstByteOfEachRow);
    }
}
