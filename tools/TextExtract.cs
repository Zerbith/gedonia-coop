// Collects the English texts the game builds in code (the
// "if (Localization.language == "English") ... else <Russian>" branches) as
// templates: string literals joined, everything computed in between becomes
// {0}, {1}, ... ("You have " + n + " gold" -> "You have {0} gold").
// Usage: TextExtract.exe <decompiled Assembly-CSharp dir> <out.tsv>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

static class TextExtract
{
    static readonly Regex Literal = new Regex("@?\"((?:[^\"\\\\]|\\\\.)*)\"");
    static readonly Regex Cyrillic = new Regex("[\u0400-\u04FF]");

    static int Main(string[] args)
    {
        HashSet<string> seen = new HashSet<string>();
        StringBuilder sb = new StringBuilder();
        foreach (string file in Directory.GetFiles(args[0], "*.cs"))
        {
            string[] lines = File.ReadAllLines(file);
            bool[] take = new bool[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("Localization.language == \"English\"")) continue;
                // The English branch: from the check to the "else" at the same
                // indentation (or a few lines for one-liners).
                string indent = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length);
                take[i] = true;
                for (int j = i + 1; j < lines.Length && j < i + 80; j++)
                {
                    string t = lines[j];
                    if (t.StartsWith(indent + "else") || (t.StartsWith(indent + "}") && !t.StartsWith(indent + "}\t") && j > i + 1 && !(j + 1 < lines.Length && lines[j + 1].StartsWith(indent + "else")) && t.Trim() == "}" && t.Length - t.TrimStart().Length == indent.Length)) break;
                    take[j] = true;
                }
            }
            // Whole statements (may run over several lines).
            string stmt = "";
            for (int i = 0; i < lines.Length; i++)
            {
                if (!take[i]) { stmt = ""; continue; }
                stmt += " " + lines[i].Trim();
                if (!lines[i].TrimEnd().EndsWith(";") && !lines[i].TrimEnd().EndsWith("{") && !lines[i].TrimEnd().EndsWith("}")) continue;
                foreach (string part in Split(stmt)) Add(part, Path.GetFileNameWithoutExtension(file), seen, sb);
                stmt = "";
            }
        }
        File.WriteAllText(args[1], sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(seen.Count + " templates -> " + args[1]);
        return 0;
    }

    // "?:" picks: both sides of a conditional are separate texts.
    static IEnumerable<string> Split(string s)
    {
        yield return s;
    }

    static void Add(string stmt, string src, HashSet<string> seen, StringBuilder sb)
    {
        if (Cyrillic.IsMatch(stmt)) return;
        MatchCollection ms = Literal.Matches(stmt);
        if (ms.Count == 0) return;
        bool concat = stmt.Contains("string.Concat(");
        StringBuilder t = new StringBuilder();
        int n = 0;
        for (int k = 0; k < ms.Count; k++)
        {
            Match m = ms[k];
            string before = k == 0 ? Before(stmt, m.Index, concat) : stmt.Substring(ms[k - 1].Index + ms[k - 1].Length, m.Index - ms[k - 1].Index - ms[k - 1].Length);
            string between = before.Trim();
            bool joined = k == 0 ? between.Length > 0 : !(between == "+" || between == ",");
            if (k > 0 && !IsJoin(between)) { Flush(t, n, src, seen, sb); t.Length = 0; n = 0; joined = false; }
            if (joined) t.Append("{" + (n++) + "}");
            t.Append(Unescape(m.Groups[1].Value));
            if (k == ms.Count - 1)
            {
                string after = stmt.Substring(m.Index + m.Length).TrimStart();
                if (after.StartsWith("+") || (concat && after.StartsWith(",") && !after.StartsWith(", Color"))) t.Append("{" + (n++) + "}");
            }
        }
        Flush(t, n, src, seen, sb);
    }

    // Literal pieces of one text are joined by + (or , in string.Concat),
    // with computed parts in between; anything else starts a new text.
    static bool IsJoin(string between)
    {
        if (between == "+" || between == ",") return true;
        return (between.StartsWith("+") || between.StartsWith(",")) && (between.EndsWith("+") || between.EndsWith(",")) && !between.Contains(";") && !between.Contains("?") && !between.Contains(":");
    }

    static string Before(string stmt, int index, bool concat)
    {
        string b = stmt.Substring(0, index).TrimEnd();
        if (b.EndsWith("+")) return "x";
        if (concat && b.EndsWith(",") && !b.EndsWith("Concat(,")) return "x";
        return "";
    }

    static void Flush(StringBuilder t, int n, string src, HashSet<string> seen, StringBuilder sb)
    {
        string s = t.ToString();
        int letters = 0;
        foreach (char c in s) if (char.IsLetter(c)) letters++;
        if (letters < 3) return;
        string plain = Regex.Replace(s, @"\{\d+\}", "");
        // identifiers, paths, anim/tag names, PlayerPrefs keys
        if (!plain.Contains(" ") && !Regex.IsMatch(plain, @"[.!?:]$") && (n > 0 || Regex.IsMatch(plain, @"[_/]|[a-z][A-Z]|^[a-z]"))) return;
        if (Regex.IsMatch(plain, @"^[A-Za-z0-9_]+$") && n == 0 && !char.IsUpper(plain[0])) return;
        string line = s.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
        if (!seen.Add(line)) return;
        sb.Append("code:").Append(src).Append('\t').Append(line).Append('\n');
    }

    static string Unescape(string s)
    {
        return s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\t", "\t").Replace("\\\\", "\\");
    }
}
