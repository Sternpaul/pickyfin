using System;
using System.Collections.Generic;
using System.Linq;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library
{
    public class SimilarityEngine
    {
        public double CalculateSimilarity(MovieMetadata source, MovieMetadata target)
        {
            if (!source.IsClassified || !target.IsClassified)
                return 0.0;

            double score = 0.0;

            // 1. Subcategory overlap (30%)
            score += source.SubcategorySet.Jaccard(target.SubcategorySet) * 0.30;

            // 2. Mood overlap (20%)
            score += source.MoodSet.Jaccard(target.MoodSet) * 0.20;

            // 3. Theme overlap (15%)
            score += source.ThemeSet.Jaccard(target.ThemeSet) * 0.15;

            // 4. Director/Cast overlap (10%)
            score += CalculateCrewSimilarity(source, target) * 0.10;

            // 5. Narrative style match (10%)
            if (string.Equals(source.NarrativeStyle, target.NarrativeStyle, StringComparison.OrdinalIgnoreCase))
                score += 0.10;

            // 6. Era proximity (5%)
            score += CalculateEraProximity(source.ReleaseYear, target.ReleaseYear) * 0.05;

            // 7. Rating/Acclaim proximity (5%)
            score += CalculateAcclaimProximity(source.Acclaim, target.Acclaim) * 0.05;

            // 8. Intensity match (5%)
            if (string.Equals(source.Intensity, target.Intensity, StringComparison.OrdinalIgnoreCase))
                score += 0.05;

            // 9. v1.5.14: TMDB keyword overlap — objective precision signal.
            if (KeywordWeight > 0)
                score += source.KeywordSet.Jaccard(target.KeywordSet) * KeywordWeight;

            return Math.Min(1.0, score); // Range: 0.0 to 1.0
        }

        // Keyword weight for similarity (set by PlaylistEngine per config; default 0 = off).
        public double KeywordWeight { get; set; } = 0.0;

        private double CalculateCrewSimilarity(MovieMetadata a, MovieMetadata b)
        {
            var crewA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var crewB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(a.Director)) foreach (var d in a.Director.Split(',')) crewA.Add(d.Trim());
            if (!string.IsNullOrWhiteSpace(a.Cast)) foreach (var c in a.Cast.Split(',')) crewA.Add(c.Trim());

            if (!string.IsNullOrWhiteSpace(b.Director)) foreach (var d in b.Director.Split(',')) crewB.Add(d.Trim());
            if (!string.IsNullOrWhiteSpace(b.Cast)) foreach (var c in b.Cast.Split(',')) crewB.Add(c.Trim());

            if (crewA.Count == 0 || crewB.Count == 0) return 0.0;

            var intersection = crewA.Intersect(crewB).Count();
            
            // Just one shared actor/director is a strong signal, so we don't use strict Jaccard here.
            // Let's cap at 3 shared people for a 1.0 score.
            return Math.Min(1.0, intersection / 3.0);
        }

        private double CalculateEraProximity(int? yearA, int? yearB)
        {
            if (!yearA.HasValue || !yearB.HasValue) return 0.0;
            
            int diff = Math.Abs(yearA.Value - yearB.Value);
            
            if (diff <= 3) return 1.0;
            if (diff <= 10) return 0.7;
            if (diff <= 20) return 0.3;
            return 0.0;
        }

        private double CalculateAcclaimProximity(double scoreA, double scoreB)
        {
            if (scoreA <= 0 || scoreB <= 0) return 0.0;
            
            double diff = Math.Abs(scoreA - scoreB);
            
            if (diff <= 1) return 1.0;
            if (diff <= 3) return 0.5;
            return 0.0;
        }
    }
}
