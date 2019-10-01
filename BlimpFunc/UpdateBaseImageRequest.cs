using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace blimp
{
    public class UpdateBaseImageRequest
    {
        [JsonProperty("newBaseImage")]
        public String NewBaseImage;
        public String stack;
    }
}
