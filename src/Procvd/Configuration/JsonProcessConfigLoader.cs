// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.IO.Pipelines;
using System.Text.Json;
using Itexoft.Threading;

namespace Procvd.Configuration;

public sealed class JsonProcessConfigLoader(JsonSerializerOptions? options = null) : IProcessConfigLoader
{
    private readonly JsonSerializerOptions optionsInternal = options ?? CreateDefaultOptions();

    public async ValueTask<ProcessConfig> LoadAsync(Stream stream, CancelToken token = default)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        using (token.Bridge(out var cancellationToken))
        {
            var reader = PipeReader.Create(stream);

            try
            {
                var config = await JsonSerializer.DeserializeAsync<ProcessConfig>(
                    reader,
                    this.optionsInternal,
                    cancellationToken).ConfigureAwait(false);

                if (config is null)
                    throw new ProcessConfigException("config deserialized to null");

                return config;
            }
            finally
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
        }
    }

    public static JsonSerializerOptions CreateDefaultOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
