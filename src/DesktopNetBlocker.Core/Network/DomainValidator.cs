using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopNetBlocker.Core.Network;

public static class DomainValidator
{
    private static readonly IdnMapping Idn = new();
    private static readonly Regex ValidHostnameRegex = new(
        @"^(\*\.)?([a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Cleans and extracts the pure domain name from user input, removing protocols (http://, https://),
    /// trailing paths, ports, and queries.
    /// </summary>
    public static string NormalizeDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string clean = input.Trim().ToLowerInvariant();

        // Remove protocol if present
        if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            clean = clean["http://".Length..];
        else if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            clean = clean["https://".Length..];

        // Remove trailing slashes and paths
        int slashIdx = clean.IndexOf('/');
        if (slashIdx >= 0)
            clean = clean[..slashIdx];

        // Remove query parameters or fragments if still present
        int queryIdx = clean.IndexOfAny(['?', '#']);
        if (queryIdx >= 0)
            clean = clean[..queryIdx];

        // Remove port if present (but not IPv6 bracketed address)
        if (!clean.StartsWith('[') && clean.Contains(':'))
        {
            int colonIdx = clean.IndexOf(':');
            clean = clean[..colonIdx];
        }

        clean = clean.Trim().TrimEnd('.');

        // Convert internationalized domain name to ASCII punycode if needed
        try
        {
            if (clean.StartsWith("*."))
            {
                string remainder = clean[2..];
                string asciiRemainder = Idn.GetAscii(remainder);
                clean = "*." + asciiRemainder;
            }
            else
            {
                clean = Idn.GetAscii(clean);
            }
        }
        catch
        {
            // If IDN conversion fails, keep original for regex validation to reject
        }

        return clean;
    }

    /// <summary>
    /// Validates whether the normalized string is a valid domain or wildcard domain.
    /// </summary>
    public static bool IsValidDomain(string domain, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(domain))
        {
            errorMessage = "Domain cannot be empty.";
            return false;
        }

        if (domain.Length > 253)
        {
            errorMessage = "Domain exceeds maximum length of 253 characters.";
            return false;
        }

        // Wildcard or standard domain syntax check
        if (!ValidHostnameRegex.IsMatch(domain))
        {
            errorMessage = $"'{domain}' is not a valid domain name or wildcard (e.g. example.com or *.example.com).";
            return false;
        }

        // Verify label length (max 63 per label)
        string check = domain.StartsWith("*.") ? domain[2..] : domain;
        string[] labels = check.Split('.');
        foreach (string label in labels)
        {
            if (label.Length is < 1 or > 63)
            {
                errorMessage = "Domain label must be between 1 and 63 characters.";
                return false;
            }
            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                errorMessage = "Domain labels cannot start or end with a hyphen.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses a batch text containing multiple domain entries (separated by newlines, commas, or semicolons).
    /// Returns validated normalized domains and any validation errors.
    /// </summary>
    public static (List<string> ValidDomains, List<string> Errors) ParseBatch(string batchText)
    {
        var valid = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(batchText))
            return (valid, errors);

        string[] tokens = batchText.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            string normalized = NormalizeDomain(token);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (IsValidDomain(normalized, out string? err))
            {
                if (seen.Add(normalized))
                {
                    valid.Add(normalized);
                }
            }
            else
            {
                errors.Add(err ?? $"Invalid domain: {token}");
            }
        }

        return (valid, errors);
    }
}

