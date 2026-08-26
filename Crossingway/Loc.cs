using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Crossingway;

// Minimal self-contained localization helper mirroring ECommons.LanguageHelpers:
// same ini format (English==translation, one entry per line, literal \n escapes,
// ?? positional placeholders) and the same .Loc() string extension name.
// Crossingway does not reference ECommons at all, so we ship this tiny equivalent
// instead of pulling in the full library just for loc. Swapping to real ECommons
// later is drop-in because the store format is identical.
public static class Localization
{
	private static readonly Dictionary<string, string> Translations = new();

	public static void Init(string? directory)
	{
		Translations.Clear();
		if (directory == null)
			return;
		string path = Path.Combine(directory, "LanguageChineseTraditional.ini");
		try
		{
			if (!File.Exists(path))
				return;
			foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
			{
				if (string.IsNullOrWhiteSpace(line))
					continue;
				int idx = line.IndexOf("==", StringComparison.Ordinal);
				if (idx <= 0)
					continue;
				string key = line[..idx].Replace("\\n", "\n");
				string value = line[(idx + 2)..].TrimEnd('\r').Replace("\\n", "\n");
				Translations[key] = value;
			}

			Services.PluginLog.Info($"Localization: loaded {Translations.Count} entries from {path}");
		}
		catch (Exception ex)
		{
			Services.PluginLog.Error(ex, $"Localization: failed to load {path}");
		}
	}

	public static string Loc(this string s) => Translations.TryGetValue(s, out string? t) ? t : s;

	public static string Loc(this string s, params object?[] args)
	{
		string result = s.Loc();
		foreach (object? a in args)
		{
			int idx = result.IndexOf("??", StringComparison.Ordinal);
			if (idx < 0)
				break;
			result = result.Remove(idx, 2).Insert(idx, a?.ToString() ?? "");
		}

		return result;
	}
}
