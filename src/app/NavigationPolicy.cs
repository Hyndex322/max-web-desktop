using System;
using System.Collections.Generic;

namespace MaxMessenger
{
    internal enum ExternalLinkPolicy
    {
        NotExternal,
        HttpBrowser,
        SpecialScheme,
        Blocked
    }

    /// <summary>
    /// Явный список источников, которым разрешено загружаться в основном окне.
    /// Разрешены только HTTPS и только точные источники (без подстановочных доменов).
    /// </summary>
    internal static class NavigationPolicy
    {
        // Проверено: web.max.ru — само приложение MAX; max.ru и www.max.ru —
        // официальные страницы, на которые MAX может перенаправить пользователя.
        private static readonly HashSet<string> AllowedNavigationOrigins =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "https://web.max.ru",
                "https://max.ru",
                "https://www.max.ru"
            };

        // Разрешения выдаются только самому приложению MAX.
        private static readonly HashSet<string> TrustedPermissionOrigins =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "https://web.max.ru"
            };

        // Специальные схемы, которые можно передать системному обработчику
        // только по явному действию пользователя.
        private static readonly HashSet<string> AllowedSpecialSchemes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mailto",
                "tel"
            };

        public static string GetOrigin(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return null;

            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed))
                return null;

            if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
                return null;

            string authority = parsed.Host.ToLowerInvariant();
            if (!parsed.IsDefaultPort)
                authority += ":" + parsed.Port;

            return parsed.Scheme.ToLowerInvariant() + "://" + authority;
        }

        public static bool IsAllowedNavigation(string uri)
        {
            string origin = GetOrigin(uri);
            return origin != null &&
                   uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                   AllowedNavigationOrigins.Contains(origin);
        }

        public static bool IsTrustedPermissionOrigin(string uri)
        {
            string origin = GetOrigin(uri);
            return origin != null &&
                   uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                   TrustedPermissionOrigins.Contains(origin);
        }

        public static ExternalLinkPolicy ClassifyExternalLink(string uri, bool userInitiated)
        {
            if (string.IsNullOrEmpty(uri))
                return ExternalLinkPolicy.Blocked;

            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed))
                return ExternalLinkPolicy.Blocked;

            if (IsAllowedNavigation(uri))
                return ExternalLinkPolicy.NotExternal;

            if (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            {
                // Автоматические переходы и редиректы на сторонние сайты не открываем.
                return userInitiated
                    ? ExternalLinkPolicy.HttpBrowser
                    : ExternalLinkPolicy.Blocked;
            }

            if (AllowedSpecialSchemes.Contains(parsed.Scheme))
                return userInitiated
                    ? ExternalLinkPolicy.SpecialScheme
                    : ExternalLinkPolicy.Blocked;

            // file:, javascript:, data:, vbscript:, ms-* и прочие схемы — запрещены.
            return ExternalLinkPolicy.Blocked;
        }

        public static string SafeOriginForLog(string uri)
        {
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
    }
}
