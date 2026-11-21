using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MaxMessenger
{
    /// <summary>
    /// Проверка политики навигации без запуска WebView2.
    /// Используется протоколом приёмки: Max.exe /policy-test result.txt
    /// </summary>
    internal static class PolicySelfTest
    {
        public static int Run(string resultPath)
        {
            var lines = new List<string>();
            int failures = 0;

            string[] allowed =
            {
                "https://web.max.ru/",
                "https://web.max.ru/login?next=%2Fchats",
                "https://max.ru/",
                "https://www.max.ru/"
            };

            string[] blocked =
            {
                "https://example.com/",
                "https://max.ru.evil.example/",
                "https://evil.max.ru/",
                "http://web.max.ru/",
                "https://web.max.ru:8443/",
                "file:///C:/Windows/win.ini",
                "javascript:alert(1)",
                "data:text/html,test"
            };

            foreach (string uri in allowed)
            {
                bool ok = NavigationPolicy.IsAllowedNavigation(uri);
                lines.Add("navigation_allowed|" + uri + "|" + ok);
                if (!ok) failures++;
            }

            foreach (string uri in blocked)
            {
                bool ok = !NavigationPolicy.IsAllowedNavigation(uri);
                lines.Add("navigation_blocked|" + uri + "|" + ok);
                if (!ok) failures++;
            }

            lines.Add("permission_trusted|https://web.max.ru/|" +
                      NavigationPolicy.IsTrustedPermissionOrigin("https://web.max.ru/"));
            if (!NavigationPolicy.IsTrustedPermissionOrigin("https://web.max.ru/")) failures++;

            lines.Add("permission_untrusted|https://max.ru/|" +
                      (!NavigationPolicy.IsTrustedPermissionOrigin("https://max.ru/")));
            if (NavigationPolicy.IsTrustedPermissionOrigin("https://max.ru/")) failures++;

            lines.Add("external_user_https|" +
                      NavigationPolicy.ClassifyExternalLink("https://example.com/", true));
            if (NavigationPolicy.ClassifyExternalLink("https://example.com/", true) != ExternalLinkPolicy.HttpBrowser)
                failures++;

            lines.Add("external_redirect_https|" +
                      NavigationPolicy.ClassifyExternalLink("https://example.com/", false));
            if (NavigationPolicy.ClassifyExternalLink("https://example.com/", false) != ExternalLinkPolicy.Blocked)
                failures++;

            lines.Add("external_user_mailto|" +
                      NavigationPolicy.ClassifyExternalLink("mailto:test@example.com", true));
            if (NavigationPolicy.ClassifyExternalLink("mailto:test@example.com", true) != ExternalLinkPolicy.SpecialScheme)
                failures++;

            lines.Add("external_file|" +
                      NavigationPolicy.ClassifyExternalLink("file:///C:/Windows/win.ini", true));
            if (NavigationPolicy.ClassifyExternalLink("file:///C:/Windows/win.ini", true) != ExternalLinkPolicy.Blocked)
                failures++;

            lines.Add("RESULT|" + (failures == 0 ? "OK" : "FAIL") + "|failures=" + failures);

            try
            {
                File.WriteAllLines(resultPath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch
            {
                return 2;
            }

            return failures == 0 ? 0 : 1;
        }
    }
}
