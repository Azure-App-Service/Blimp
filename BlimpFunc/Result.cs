using System.Collections.Generic;
using Newtonsoft.Json;

namespace Blimp
{
    public class Result
    {
        [JsonProperty("name")]
        public string Name;

        [JsonProperty("last_updated")]
        public string LastUpdated;
    }
}
