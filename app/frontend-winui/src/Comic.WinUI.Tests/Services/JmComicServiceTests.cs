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

    [TestMethod]
    public void DecryptPayload_UsesSuppliedDataSecretInsteadOfDefault()
    {
        // 站点密钥集中在 JmSiteOptions 里,注入自定义配置必须真正生效。
        const long timestamp = 1_700_000_000;
        const string customSecret = "custom-data-secret";
        var plaintext = Encoding.UTF8.GetBytes("""{"id":7}""");
        var encrypted = EncryptWithSecret(timestamp, customSecret, plaintext);

        var result = JmComicService.DecryptPayload(timestamp, encrypted, customSecret);

        CollectionAssert.AreEqual(plaintext, result);

        // 用默认密钥解同一段密文不可能得到相同明文,否则说明 dataSecret 参数没被采纳。
        // 注意不能直接断言抛异常:错密钥解出的随机字节有约 1/256 的概率恰好通过
        // PKCS#7 校验,那样断言就成了偶发失败。
        byte[]? decodedWithDefaultSecret = null;
        try
        {
            decodedWithDefaultSecret = JmComicService.DecryptPayload(timestamp, encrypted);
        }
        catch (InvalidDataException)
        {
            // 填充校验失败是最常见的结果,同样说明默认密钥解不开。
        }

        if (decodedWithDefaultSecret is not null)
        {
            CollectionAssert.AreNotEqual(plaintext, decodedWithDefaultSecret);
        }
    }

    [TestMethod]
    public void SanitizeFileName_ReplacesIllegalCharactersAndTrimsEdges()
    {
        Assert.AreEqual("a_b_c", JmComicService.SanitizeFileName("a/b\\c"));
        Assert.AreEqual("标题", JmComicService.SanitizeFileName("  .标题. "));
        Assert.AreEqual("unnamed", JmComicService.SanitizeFileName("   "));
        Assert.AreEqual("unnamed", JmComicService.SanitizeFileName("..."));
    }

    [TestMethod]
    public void SanitizeFileName_EscapesWindowsReservedDeviceNames()
    {
        // 标题来自站点,叫 NUL 或 CON.jpg 会让 CreateDirectory/File.Create 直接失败。
        Assert.AreEqual("_NUL", JmComicService.SanitizeFileName("NUL"));
        Assert.AreEqual("_nul.jpg", JmComicService.SanitizeFileName("nul.jpg"));
        Assert.AreEqual("_CON.txt", JmComicService.SanitizeFileName("CON.txt"));
        Assert.AreEqual("_COM1", JmComicService.SanitizeFileName("COM1"));
        Assert.AreEqual("_LPT9", JmComicService.SanitizeFileName("LPT9"));
        // 只是以保留名开头的普通标题不能被改写。
        Assert.AreEqual("CONTENT", JmComicService.SanitizeFileName("CONTENT"));
        Assert.AreEqual("COM10", JmComicService.SanitizeFileName("COM10"));
    }

    [TestMethod]
    public void SanitizeFileName_TruncatesWithoutSplittingSurrogatePairs()
    {
        // 第 200 个码元正好落在 emoji 的高代理项上,直接切会留下孤立代理项。
        var value = new string('a', 199) + "😀" + new string('b', 50);
        Assert.IsTrue(value.Length > 200);

        var result = JmComicService.SanitizeFileName(value);

        Assert.AreEqual(199, result.Length);
        Assert.IsFalse(result.Any(char.IsSurrogate), "截断结果不应包含孤立代理项。");
        Assert.AreEqual(new string('a', 199), result);
    }

    [TestMethod]
    public void SanitizeFileName_KeepsWholeEmojiWhenPairFitsInsideLimit()
    {
        var value = new string('a', 198) + "😀" + new string('b', 50);

        var result = JmComicService.SanitizeFileName(value);

        Assert.AreEqual(200, result.Length);
        Assert.AreEqual(new string('a', 198) + "😀", result);
    }

    private static string EncryptWithSecret(long timestamp, string secret, byte[] plaintext)
    {
        var keyText = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + secret))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
    }
}
