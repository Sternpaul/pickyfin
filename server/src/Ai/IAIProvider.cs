using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Ai
{
    public interface IAIProvider
    {
        string Name { get; }
        
        /// <summary>
        /// Validates if the configured API key is valid.
        /// </summary>
        Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a batch of movies to the AI model for classification.
        /// Returns a structured JSON string containing subcategories, moods, themes, etc.
        /// </summary>
        Task<string> ClassifyMoviesAsync(
            List<MovieMetadata> movies, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a user chat query and returns the AI's response.
        /// </summary>
        Task<string> ChatAsync(
            string userQuery, 
            string systemPrompt, 
            CancellationToken cancellationToken = default);
    }
}
