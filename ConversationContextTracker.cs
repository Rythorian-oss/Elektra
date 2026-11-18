/*
 * Creator: Justin L. Ross | Farmington, Maine. | Remote Location: Iceland | Black Star Research Facility.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Elektra.Knowledge.Models;

namespace Elektra.Knowledge.Context;

public enum EntityCategory
{
    Unknown = 0,
    Person = 1,
    ThingOrPlace = 2,
    Group = 3
}

public sealed record ConversationTurn(
    string RawQuery,
    string ResolvedQuery,
    string PrimaryEntity,
    EntityCategory Category,
    string AnswerSnippet,
    long TimestampTicks);

public sealed partial class ConversationContextTracker
{
    private const int MaxHistoryTurns = 4;
    private static readonly TimeSpan ContextTtl = TimeSpan.FromMinutes(3);

    private readonly Lock _lock = new();
    private readonly LinkedList<ConversationTurn> _history = new();

    [GeneratedRegex(@"\b(he|him|his|himself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MasculinePronounRegex();

    [GeneratedRegex(@"\b(she|her|hers|herself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FemininePronounRegex();

    [GeneratedRegex(@"\b(it|its|itself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NeuterPronounRegex();

    [GeneratedRegex(@"\b(they|them|their|theirs)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PluralPronounRegex();

    [GeneratedRegex(@"^(and|what about|how about)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EllipticalFollowUpRegex();

    public string ExpandQueryWithContext(string rawQuery, out string? resolvedEntity)
    {
        resolvedEntity = null;
        string trimmed = rawQuery.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        lock (_lock)
        {
            PruneExpiredLocked();
            if (_history.Count == 0)
                return trimmed;

            if (MasculinePronounRegex().IsMatch(trimmed) || FemininePronounRegex().IsMatch(trimmed))
            {
                var personTurn = FindMostRecentLocked(t => t.Category is EntityCategory.Person or EntityCategory.Unknown);
                if (personTurn != null)
                {
                    resolvedEntity = personTurn.PrimaryEntity;
                    return ReplacePronouns(trimmed, personTurn.PrimaryEntity);
                }
            }

            if (NeuterPronounRegex().IsMatch(trimmed))
            {
                var thingTurn = FindMostRecentLocked(t => t.Category is EntityCategory.ThingOrPlace or EntityCategory.Unknown);
                if (thingTurn != null)
                {
                    resolvedEntity = thingTurn.PrimaryEntity;
                    return ReplacePronouns(trimmed, thingTurn.PrimaryEntity);
                }
            }

            if (PluralPronounRegex().IsMatch(trimmed))
            {
                var lastTurn = _history.First?.Value;
                if (lastTurn != null)
                {
                    resolvedEntity = lastTurn.PrimaryEntity;
                    return ReplacePronouns(trimmed, lastTurn.PrimaryEntity);
                }
            }

            var ellipticalMatch = EllipticalFollowUpRegex().Match(trimmed);
            if (ellipticalMatch.Success && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5)
            {
                var lastTurn = _history.First?.Value;
                if (lastTurn != null)
                {
                    resolvedEntity = lastTurn.PrimaryEntity;
                    string attribute = ellipticalMatch.Groups[2].Value.TrimEnd('?', '.');
                    return $"{lastTurn.PrimaryEntity} {attribute}";
                }
            }

            return trimmed;
        }
    }

    public void RecordTurn(string rawQuery, string resolvedQuery, QueryResponse response)
    {
        if (response.Source is "Fallback" || response.Source.StartsWith("Math", StringComparison.OrdinalIgnoreCase))
            return;

        string? entity = ExtractPrimaryEntity(resolvedQuery, response.Answer);
        if (string.IsNullOrWhiteSpace(entity))
            return;

        var turn = new ConversationTurn(
            RawQuery: rawQuery,
            ResolvedQuery: resolvedQuery,
            PrimaryEntity: entity,
            Category: InferCategory(response.Answer),
            AnswerSnippet: response.Answer,
            TimestampTicks: Stopwatch.GetTimestamp());

        lock (_lock)
        {
            PruneExpiredLocked();
            _history.AddFirst(turn);
            while (_history.Count > MaxHistoryTurns)
                _history.RemoveLast();
        }
    }

    public void Clear()
    {
        lock (_lock) _history.Clear();
    }

    private ConversationTurn? FindMostRecentLocked(Func<ConversationTurn, bool> predicate)
    {
        foreach (var turn in _history)
        {
            if (predicate(turn))
                return turn;
        }
        return null;
    }

    private void PruneExpiredLocked()
    {
        long now = Stopwatch.GetTimestamp();
        long maxAgeTicks = (long)(ContextTtl.TotalSeconds * Stopwatch.Frequency);
        while (_history.Last != null && (now - _history.Last.Value.TimestampTicks) > maxAgeTicks)
            _history.RemoveLast();
    }

    private static string ReplacePronouns(string query, string entity)
    {
        string result = Regex.Replace(query, @"\b(his|her|its|their)\b", $"{entity}'s", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\b(he|him|she|it|they|them)\b", entity, RegexOptions.IgnoreCase);
        return result;
    }

    private static string? ExtractPrimaryEntity(string resolvedQuery, string answer)
    {
        string q = resolvedQuery.Trim().TrimEnd('?', '.', '!');
        string[] prefixes =
        [
            "who is ", "who was ", "what is ", "what was ", "what are ",
            "tell me about ", "define ", "where is ", "how tall is ", "how old is "
        ];

        foreach (string prefix in prefixes)
        {
            if (q.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = q[prefix.Length..].Trim();
                int apostrophe = candidate.IndexOf("'s ", StringComparison.OrdinalIgnoreCase);
                if (apostrophe > 0)
                    candidate = candidate[..apostrophe].Trim();

                if (candidate.Length >= 2)
                    return ToTitleCaseIfLower(candidate);
            }
        }

        int copulaIdx = answer.IndexOf(" is ", StringComparison.Ordinal);
        if (copulaIdx < 0) copulaIdx = answer.IndexOf(" was ", StringComparison.Ordinal);
        if (copulaIdx > 1 && copulaIdx <= 50)
            return answer[..copulaIdx].Trim();

        return q.Length <= 45 ? ToTitleCaseIfLower(q) : null;
    }

    private static EntityCategory InferCategory(string answer)
    {
        if (answer.Contains(" was a ", StringComparison.OrdinalIgnoreCase) || answer.Contains(" is a ", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("born ", StringComparison.OrdinalIgnoreCase) || answer.Contains("actor", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("scientist", StringComparison.OrdinalIgnoreCase) || answer.Contains("politician", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("singer", StringComparison.OrdinalIgnoreCase) || answer.Contains("author", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(" he ", StringComparison.OrdinalIgnoreCase) || answer.Contains(" she ", StringComparison.OrdinalIgnoreCase))
            return EntityCategory.Person;

        if (answer.Contains("city", StringComparison.OrdinalIgnoreCase) || answer.Contains("country", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("company", StringComparison.OrdinalIgnoreCase) || answer.Contains("planet", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("capital", StringComparison.OrdinalIgnoreCase) || answer.Contains("located in", StringComparison.OrdinalIgnoreCase))
            return EntityCategory.ThingOrPlace;

        return EntityCategory.Unknown;
    }

    private static string ToTitleCaseIfLower(string text)
    {
        foreach (char character in text)
        {
            if (char.IsUpper(character))
                return text;
        }
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
    }
}