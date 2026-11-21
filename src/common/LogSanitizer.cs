using System;
using System.Text.RegularExpressions;

namespace MaxCommon
{
    /// <summary>
    /// Убирает из журналов полные URL (оставляя домен), query-параметры
    /// и полные локальные пути. Коды ошибок и типы событий сохраняются.
    /// </summary>
    internal static class LogSanitizer
    {
        private static readonly Regex UrlPattern = new Regex(
            @"(?i)\b(https?)://([^/\s?#]+)([^\s""']*)",
            RegexOptions.Compiled);

        private static readonly Regex OtherSchemePattern = new Regex(
            @"(?i)\b(file|javascript|data|vbscript|ms-[a-z0-9-]+|blob):[^\s""']*",
            RegexOptions.Compiled);

        private static readonly Regex LocalPathPattern = new Regex(
            @"(?i)(?<![\w])(?:[a-z]:\\|\\\\)[^\r\n""']*",
            RegexOptions.Compiled);

        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            // URL: оставляем только схему и домен.
            value = UrlPattern.Replace(
                value,
                delegate(Match match)
                {
                    return match.Groups[1].Value + "://" + match.Groups[2].Value + "/...";
                });

            // Опасные и специальные схемы не должны попадать в журнал целиком.
            value = OtherSchemePattern.Replace(
                value,
                delegate(Match match)
                {
                    return match.Groups[1].Value + ":<redacted>";
                });

            // Полные локальные и UNC-пути.
            value = LocalPathPattern.Replace(value, "<local-path>");

            return value;
        }

        public static string SafeOrigin(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return "<empty>";

            try
            {
                var parsed = new Uri(uri);
                if (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                    return parsed.Scheme + "://" + parsed.Host.ToLowerInvariant();
                return parsed.Scheme + ":<redacted>";
            }
            catch
            {
                return "<invalid-uri>";
            }
        }

        public static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "<empty>";

            try { return System.IO.Path.GetFileName(path); }
            catch { return "<invalid-name>"; }
        }
    }
}
