using System.Globalization;
using System.Text;

namespace CineKros.TextNormalization;

public static class SerbianLatinNormalizer
{
    public static string Version => "serbian-latin-nfc-whitespace-v1";

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var source = value.Normalize(NormalizationForm.FormC);
        var runes = source.EnumerateRunes().ToArray();
        var result = new StringBuilder(source.Length);

        for (var index = 0; index < runes.Length;)
        {
            var rune = runes[index];

            if (Rune.IsWhiteSpace(rune))
            {
                if (result.Length > 0 && result[^1] != ' ')
                {
                    result.Append(' ');
                }

                index++;
                continue;
            }

            if (Rune.IsLetter(rune))
            {
                var wordEnd = index + 1;
                var allUppercase = Rune.IsUpper(rune);
                while (wordEnd < runes.Length && Rune.IsLetter(runes[wordEnd]))
                {
                    allUppercase &= Rune.IsUpper(runes[wordEnd]);
                    wordEnd++;
                }

                for (; index < wordEnd; index++)
                {
                    AppendSerbianLatin(result, runes[index], allUppercase);
                }

                continue;
            }

            AppendSerbianLatin(result, rune, allUppercaseWord: false);
            index++;
        }

        return result.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    private static void AppendSerbianLatin(StringBuilder output, Rune rune, bool allUppercaseWord)
    {
        var uppercase = Rune.IsUpper(rune);
        var letter = rune.Value switch
        {
            0x0430 or 0x0410 => uppercase ? "A" : "a", // а
            0x0431 or 0x0411 => uppercase ? "B" : "b", // б
            0x0432 or 0x0412 => uppercase ? "V" : "v", // в
            0x0433 or 0x0413 => uppercase ? "G" : "g", // г
            0x0434 or 0x0414 => uppercase ? "D" : "d", // д
            0x0452 or 0x0402 => uppercase ? "Đ" : "đ", // ђ
            0x0435 or 0x0415 => uppercase ? "E" : "e", // е
            0x0436 or 0x0416 => uppercase ? "Ž" : "ž", // ж
            0x0437 or 0x0417 => uppercase ? "Z" : "z", // з
            0x0438 or 0x0418 => uppercase ? "I" : "i", // и
            0x0458 or 0x0408 => uppercase ? "J" : "j", // ј
            0x043A or 0x041A => uppercase ? "K" : "k", // к
            0x043B or 0x041B => uppercase ? "L" : "l", // л
            0x0459 or 0x0409 => uppercase ? allUppercaseWord ? "LJ" : "Lj" : "lj", // љ
            0x043C or 0x041C => uppercase ? "M" : "m", // м
            0x043D or 0x041D => uppercase ? "N" : "n", // н
            0x045A or 0x040A => uppercase ? allUppercaseWord ? "NJ" : "Nj" : "nj", // њ
            0x043E or 0x041E => uppercase ? "O" : "o", // о
            0x043F or 0x041F => uppercase ? "P" : "p", // п
            0x0440 or 0x0420 => uppercase ? "R" : "r", // р
            0x0441 or 0x0421 => uppercase ? "S" : "s", // с
            0x0442 or 0x0422 => uppercase ? "T" : "t", // т
            0x045B or 0x040B => uppercase ? "Ć" : "ć", // ћ
            0x0443 or 0x0423 => uppercase ? "U" : "u", // у
            0x0444 or 0x0424 => uppercase ? "F" : "f", // ф
            0x0445 or 0x0425 => uppercase ? "H" : "h", // х
            0x0446 or 0x0426 => uppercase ? "C" : "c", // ц
            0x0447 or 0x0427 => uppercase ? "Č" : "č", // ч
            0x045F or 0x040F => uppercase ? allUppercaseWord ? "DŽ" : "Dž" : "dž", // џ
            0x0448 or 0x0428 => uppercase ? "Š" : "š", // ш
            _ => null
        };

        if (letter is null)
        {
            output.Append(rune.ToString());
        }
        else
        {
            output.Append(letter);
        }
    }
}
