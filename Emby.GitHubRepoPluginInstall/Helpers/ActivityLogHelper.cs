using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Web;

namespace Emby.GitHubRepoPluginInstall.Helpers;

public static class ActivityLogHelper
{
    // Emby shows this HTML with its whitespace, so the indentation used to lay the templates out in code showed up
    // as large blank gaps in the activity log. Whitespace between tags means nothing here, so it is removed.
    // Text inside tags, including the contents of <pre> blocks, is left alone.
    public static string Compact(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        // Set <pre> blocks aside, since their whitespace is the point. A tag-shaped placeholder takes their place
        // so the squashing below treats it like any other tag.
        var blocks = new List<string>();
        var masked = PreBlock.Replace(html, m =>
        {
            blocks.Add(m.Value);
            return $"<x-pre-{blocks.Count - 1}/>";
        });

        // Line breaks and the indentation after them become one space, and space between two tags goes entirely
        masked = Regex.Replace(masked, @"\s*[\r\n]+\s*", " ");
        masked = Regex.Replace(masked, @">\s+<", "><");

        for (var i = 0; i < blocks.Count; i++)
            masked = masked.Replace($"<x-pre-{i}/>", blocks[i]);

        return masked.Trim();
    }

    private static readonly Regex PreBlock = new Regex(@"<pre\b[^>]*>.*?</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static string CreateSuccessHtml(string title, string message, string version = null, string details = null)
    {
        var html = $@"
        <div style='font-family: ""Segoe UI"", Arial, sans-serif; line-height: 1.6; color: #333;'>
            <div style='background: linear-gradient(135deg, #28a745, #20c997); color: white; padding: 12px 16px; border-radius: 6px 6px 0 0; margin-bottom: 2px;'>
                <div style='display: flex; align-items: center; gap: 8px;'>
                    <span style='font-size: 18px;'>✅</span>
                    <strong style='font-size: 16px;'>{HttpUtility.HtmlEncode(title)}</strong>
                </div>
            </div>
            <div style='background: #f8f9fa; border: 1px solid #e9ecef; border-top: none; padding: 16px; border-radius: 0 0 6px 6px;'>
                <div style='margin-bottom: 12px;'>
                    <span style='font-weight: 600; color: #28a745;'>Status:</span> {HttpUtility.HtmlEncode(message)}
                </div>";

        if (!string.IsNullOrEmpty(version))
        {
            html += $@"
                <div style='margin-bottom: 12px;'>
                    <span style='font-weight: 600; color: #007bff;'>Version:</span> 
                    <code style='background: #e9ecef; padding: 2px 6px; border-radius: 3px; font-family: ""Consolas"", monospace;'>{HttpUtility.HtmlEncode(version)}</code>
                </div>";
        }

        if (!string.IsNullOrEmpty(details))
        {
            html += $@"
                <div style='margin-top: 16px;'>
                    <details style='cursor: pointer;'>
                        <summary style='font-weight: 600; color: #6c757d; user-select: none;'>Release Details</summary>
                        <div style='margin-top: 8px; padding: 12px; background: white; border-left: 4px solid #007bff; border-radius: 0 4px 4px 0;'>
                            <pre style='margin: 0; white-space: pre-wrap; font-family: ""Consolas"", monospace; font-size: 14px; color: #495057;'>{HttpUtility.HtmlEncode(details)}</pre>
                        </div>
                    </details>
                </div>";
        }

        html += @"
            </div>
        </div>";

        return Compact(html);
    }

    public static string CreateWarningHtml(string title, string message, string details = null)
    {
        var html = $@"
        <div style='font-family: ""Segoe UI"", Arial, sans-serif; line-height: 1.6; color: #333;'>
            <div style='background: linear-gradient(135deg, #ffc107, #fd7e14); color: #212529; padding: 12px 16px; border-radius: 6px 6px 0 0; margin-bottom: 2px;'>
                <div style='display: flex; align-items: center; gap: 8px;'>
                    <span style='font-size: 18px;'>⚠️</span>
                    <strong style='font-size: 16px;'>{HttpUtility.HtmlEncode(title)}</strong>
                </div>
            </div>
            <div style='background: #fff3cd; border: 1px solid #ffeaa7; border-top: none; padding: 16px; border-radius: 0 0 6px 6px;'>
                <div style='margin-bottom: 12px;'>
                    <span style='font-weight: 600; color: #856404;'>Warning:</span> {HttpUtility.HtmlEncode(message)}
                </div>";

        if (!string.IsNullOrEmpty(details))
        {
            html += $@"
                <div style='margin-top: 16px;'>
                    <details style='cursor: pointer;'>
                        <summary style='font-weight: 600; color: #856404; user-select: none;'>Details</summary>
                        <div style='margin-top: 8px; padding: 12px; background: white; border-left: 4px solid #ffc107; border-radius: 0 4px 4px 0;'>
                            <pre style='margin: 0; white-space: pre-wrap; font-family: ""Consolas"", monospace; font-size: 14px; color: #495057;'>{HttpUtility.HtmlEncode(details)}</pre>
                        </div>
                    </details>
                </div>";
        }

        html += @"
            </div>
        </div>";

        return Compact(html);
    }

    public static string CreateErrorHtml(string title, string message, string error = null, string stackTrace = null)
    {
        var html = $@"
        <div style='font-family: ""Segoe UI"", Arial, sans-serif; line-height: 1.6; color: #333;'>
            <div style='background: linear-gradient(135deg, #dc3545, #c82333); color: white; padding: 12px 16px; border-radius: 6px 6px 0 0; margin-bottom: 2px;'>
                <div style='display: flex; align-items: center; gap: 8px;'>
                    <span style='font-size: 18px;'>❌</span>
                    <strong style='font-size: 16px;'>{HttpUtility.HtmlEncode(title)}</strong>
                </div>
            </div>
            <div style='background: #f8d7da; border: 1px solid #f5c6cb; border-top: none; padding: 16px; border-radius: 0 0 6px 6px;'>
                <div style='margin-bottom: 12px;'>
                    <span style='font-weight: 600; color: #721c24;'>Error:</span> {HttpUtility.HtmlEncode(message)}
                </div>";

        if (!string.IsNullOrEmpty(error))
        {
            html += $@"
                <div style='margin-bottom: 12px;'>
                    <span style='font-weight: 600; color: #721c24;'>Message:</span>
                    <code style='background: white; padding: 4px 8px; border-radius: 3px; font-family: ""Consolas"", monospace; display: block; margin-top: 4px; border-left: 4px solid #dc3545;'>{HttpUtility.HtmlEncode(error)}</code>
                </div>";
        }

        if (!string.IsNullOrEmpty(stackTrace))
        {
            html += $@"
                <div style='margin-top: 16px;'>
                    <details style='cursor: pointer;'>
                        <summary style='font-weight: 600; color: #721c24; user-select: none;'>Stack Trace</summary>
                        <div style='margin-top: 8px; padding: 12px; background: white; border-left: 4px solid #dc3545; border-radius: 0 4px 4px 0; max-height: 200px; overflow-y: auto;'>
                            <pre style='margin: 0; white-space: pre-wrap; font-family: ""Consolas"", monospace; font-size: 12px; color: #495057;'>{HttpUtility.HtmlEncode(stackTrace)}</pre>
                        </div>
                    </details>
                </div>";
        }

        html += @"
            </div>
        </div>";

        return Compact(html);
    }

    public static string CreateInfoHtml(string title, string message, string details = null)
    {
        var html = $@"
        <div style='font-family: ""Segoe UI"", Arial, sans-serif; line-height: 1.6; color: #333;'>
            <div style='background: linear-gradient(135deg, #007bff, #6610f2); color: white; padding: 12px 16px; border-radius: 6px 6px 0 0; margin-bottom: 2px;'>
                <div style='display: flex; align-items: center; gap: 8px;'>
                    <span style='font-size: 18px;'>ℹ️</span>
                    <strong style='font-size: 16px;'>{HttpUtility.HtmlEncode(title)}</strong>
                </div>
            </div>
            <div style='background: #d1ecf1; border: 1px solid #bee5eb; border-top: none; padding: 16px; border-radius: 0 0 6px 6px;'>
                <div style='margin-bottom: 12px;'>
                    <span style='font-weight: 600; color: #0c5460;'>Info:</span> {HttpUtility.HtmlEncode(message)}
                </div>";

        if (!string.IsNullOrEmpty(details))
        {
            html += $@"
                <div style='margin-top: 16px;'>
                    <details style='cursor: pointer;'>
                        <summary style='font-weight: 600; color: #0c5460; user-select: none;'>Details</summary>
                        <div style='margin-top: 8px; padding: 12px; background: white; border-left: 4px solid #007bff; border-radius: 0 4px 4px 0;'>
                            <pre style='margin: 0; white-space: pre-wrap; font-family: ""Consolas"", monospace; font-size: 14px; color: #495057;'>{HttpUtility.HtmlEncode(details)}</pre>
                        </div>
                    </details>
                </div>";
        }

        html += @"
            </div>
        </div>";

        return Compact(html);
    }

    /// <summary>One card for a whole run: counts first, then one line per plugin.</summary>
    public static string CreateRunSummaryHtml(Models.RunRecord record)
    {
        var failed = record.Failed > 0;
        var gradient = failed ? "linear-gradient(135deg, #ffc107, #fd7e14)" : "linear-gradient(135deg, #28a745, #20c997)";
        var headColor = failed ? "#212529" : "white";

        var html = $@"
        <div style='font-family: ""Segoe UI"", Arial, sans-serif; line-height: 1.6; color: #333;'>
            <div style='background: {gradient}; color: {headColor}; padding: 12px 16px; border-radius: 6px 6px 0 0; margin-bottom: 2px;'>
                <strong style='font-size: 16px;'>{record.Updated} updated · {record.UpToDate} up to date · {record.Failed} failed · {record.Waiting} waiting on you</strong>
            </div>
            <div style='background: #f8f9fa; border: 1px solid #e9ecef; border-top: none; padding: 16px; border-radius: 0 0 6px 6px;'>
                <div style='margin-bottom: 8px; color: #6c757d;'>{HttpUtility.HtmlEncode(record.Trigger)} run, took {HttpUtility.HtmlEncode(TimeFormat.Duration(record.DurationSeconds))}</div>";

        foreach (var r in record.Results)
        {
            string label;
            switch (r.Outcome)
            {
                case Models.RepoOutcome.Updated:       label = $"updated {r.FromVersion ?? "(new)"} → {r.ToVersion}"; break;
                case Models.RepoOutcome.Failed:        label = "failed: " + r.Message;                                 break;
                case Models.RepoOutcome.WaitingManual: label = $"{r.ToVersion} available, waiting on you";             break;
                case Models.RepoOutcome.NoRelease:     label = "no release published";                                  break;
                default:                               label = $"up to date ({r.ToVersion})";                           break;
            }

            html += $@"
                <div><strong>{HttpUtility.HtmlEncode(r.Name)}</strong> — {HttpUtility.HtmlEncode(label)}</div>";
        }

        html += @"
            </div>
        </div>";

        return Compact(html);
    }
}