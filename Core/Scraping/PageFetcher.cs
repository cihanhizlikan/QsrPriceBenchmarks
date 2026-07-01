using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Playwright;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Shared Playwright page-fetch helper used by Step 1 (chain website) and
/// Step 2 (TG) crawling. Dismisses common Turkish/English cookie-consent
/// banners after navigation — several chain sites otherwise overlay content
/// that blocks link/card extraction.
/// </summary>
public static class PageFetcher
{
    public static async Task<string> FetchHtmlAsync(
        IPage page, string url,
        string? waitForSelector = null,
        bool networkIdle = false,
        int navTimeoutMs = 30_000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var gotoOptions = new PageGotoOptions
        {
            WaitUntil = networkIdle ? WaitUntilState.NetworkIdle : WaitUntilState.DOMContentLoaded,
            Timeout = navTimeoutMs,
        };

        // Force in-flight Playwright operations to abort the moment cancellation
        // is requested: closing the page makes the pending GotoAsync / waits
        // throw immediately instead of running to completion. Without this the
        // token is only observed at await boundaries between whole page loads.
        await using var reg = ct.Register(() =>
        {
            try { _ = page.CloseAsync(); } catch { /* already closing */ }
        });

        await page.GotoAsync(url, gotoOptions);

        await DismissCookieBannerAsync(page);

        if (waitForSelector is not null)
        {
            try
            {
                await page.WaitForSelectorAsync(waitForSelector,
                    new PageWaitForSelectorOptions { Timeout = 8_000 });
            }
            catch (TimeoutException)
            {
                // Selector never appeared — caller decides how to handle a
                // possibly-empty page; we still return whatever HTML exists.
            }
        }

        return await page.ContentAsync();
    }

    public static async Task DismissCookieBannerAsync(IPage page)
    {
        try
        {
            // Accept the consent banner robustly. TG's banner is titled
            // "Çerez Tercihlerini Bize Bildirin" and its accept control may read
            // "Tümünü Kabul Et" / "Kabul Et" / "Onayla" etc. We click the first
            // short button/link whose text matches an accept label and does NOT
            // contain a reject/settings word — otherwise an undismissed banner's
            // <h1> ("Çerez Tercihlerini…") gets scraped as the restaurant name
            // and the address modal stays covered.
            bool clicked = await page.EvaluateAsync<bool>("""
                () => {
                    const accept = ['Tümünü Kabul Et','Tümünü Kabul','Hepsini Kabul Et',
                        'Hepsini Kabul','Çerezleri Kabul Et','Çerezleri Kabul','Kabul Ediyorum',
                        'Kabul Et','Onayla','İzin Ver','Kabul','Anladım','Tamam',
                        'Accept All','Accept Cookies','I Accept','Accept','Allow','Agree','Got it','OK'];
                    const reject = ['Etmiyorum','Reddet','Vazgeç','Yönet','Ayarlar','Tercih',
                        'Reject','Decline','Manage','Settings','Customize','Customise'];
                    const norm = s => (s || '').replace(/\s+/g, ' ').trim();
                    const els = [...document.querySelectorAll('button, a, [role="button"]')];
                    for (const el of els) {
                        const t = norm(el.innerText || el.textContent);
                        if (!t || t.length > 40) continue;
                        if (reject.some(r => t.includes(r))) continue;
                        if (accept.some(a => t === a || t.startsWith(a))) {
                            el.click();
                            return true;
                        }
                    }
                    return false;
                }
                """);
            if (clicked)
            {
                await page.WaitForTimeoutAsync(400);
            }
        }
        catch
        {
            // Non-fatal — a missing/blocked banner should never abort the crawl.
        }
    }

    /// <summary>Parse raw HTML into an AngleSharp document for querying.</summary>
    public static async Task<IDocument> ParseHtmlAsync(string html, string url)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html).Address(url));
    }
}
