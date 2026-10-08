using CineKros.TextNormalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.TextNormalization.Tests;

[TestClass]
public sealed class SerbianLatinNormalizerTests
{
    [TestMethod]
    public void ContractVersionIsStable()
    {
        Assert.AreEqual("serbian-latin-nfc-whitespace-v1", SerbianLatinNormalizer.Version);
    }

    [TestMethod]
    public void MapsAllThirtyLowercaseSerbianLetters()
    {
        Assert.AreEqual("a b v g d đ e ž z i j k l lj m n nj o p r s t ć u f h c č dž š",
            SerbianLatinNormalizer.Normalize("а б в г д ђ е ж з и ј к л љ м н њ о п р с т ћ у ф х ц ч џ ш"));
    }

    [TestMethod]
    public void MapsAllThirtyUppercaseSerbianLetters()
    {
        Assert.AreEqual("A B V G D Đ E Ž Z I J K L LJ M N NJ O P R S T Ć U F H C Č DŽ Š",
            SerbianLatinNormalizer.Normalize("А Б В Г Д Ђ Е Ж З И Ј К Л Љ М Н Њ О П Р С Т Ћ У Ф Х Ц Ч Џ Ш"));
    }

    [TestMethod]
    public void AppliesDigraphCasingAndWordBoundaries()
    {
        (string Input, string Expected)[] cases =
        [
            ("Љубав", "Ljubav"),
            ("ЉУБАВ", "LJUBAV"),
            ("Његош", "Njegoš"),
            ("ЏЕЗ", "DŽEZ"),
            ("аЉубав", "aLjubav"),
            ("аЊегош", "aNjegoš"),
            ("аЏез", "aDžez"),
            ("Љ!", "LJ!"),
            ("љ2", "lj2")
        ];

        foreach (var testCase in cases)
        {
            Assert.AreEqual(testCase.Expected, SerbianLatinNormalizer.Normalize(testCase.Input), testCase.Input);
        }
    }

    [TestMethod]
    public void PreservesLatinSerbianEnglishAndPunctuation()
    {
        const string input = "Đorđe Ćirić, već živi u Nišu! Džez & Sherlock Holmes: IMDb 3D.";

        Assert.AreEqual(input, SerbianLatinNormalizer.Normalize(input));
    }

    [TestMethod]
    public void NormalizesToNfcBeforeAndAfterTransliteration()
    {
        Assert.AreEqual("Ćirilica, café", SerbianLatinNormalizer.Normalize("Ćirilica, café"));
        Assert.AreEqual("Ć", SerbianLatinNormalizer.Normalize("Ћ"));
    }

    [TestMethod]
    public void CollapsesUnicodeWhitespaceAndTrims()
    {
        Assert.AreEqual("Ljubav i Džez", SerbianLatinNormalizer.Normalize("\u00A0Љубав\t i\r\n Џез \u2003"));
        Assert.AreEqual(string.Empty, SerbianLatinNormalizer.Normalize(" \t\r\n\u00A0\u2003"));
    }

    [TestMethod]
    public void EmptyInputReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, SerbianLatinNormalizer.Normalize(string.Empty));
    }

    [TestMethod]
    public void NullInputThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SerbianLatinNormalizer.Normalize(null!));
    }

    [TestMethod]
    public void PreservesUnrelatedScriptsAndNonWhitespaceControls()
    {
        const string input = "Кириллица Ελληνικά עברית العربية 中文 😀 \u0001";

        Assert.AreEqual("Kirillica Ελληνικά עברית العربية 中文 😀 \u0001", SerbianLatinNormalizer.Normalize(input));
    }

    [TestMethod]
    public void IsIdempotent()
    {
        const string input = "  ЉУБАВ\tи  Džez!  ";

        var once = SerbianLatinNormalizer.Normalize(input);

        Assert.AreEqual("LJUBAV i Džez!", once);
        Assert.AreEqual(once, SerbianLatinNormalizer.Normalize(once));
    }
}
