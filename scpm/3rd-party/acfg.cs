// https://raw.githubusercontent.com/alkee/acfg
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace acfg
{
    public abstract class Config
    {
        public Config(bool jsonIndent = true)
        {
            serializer = JsonSerializer.Create();
            serializer.Formatting = jsonIndent
                ? Formatting.Indented
                : Formatting.None;
        }

        public string ToJson(Config? ignoreValues = null)
        { // ignoreValues 와 같은 값을 갖는 멤버는 저장하지 않음. null 이면 전체 저장
            if (ignoreValues == null)
            {
                return Serialize(this);
            }
            var objIgnore = JObject.FromObject(ignoreValues, serializer);
            var objTarget = JObject.FromObject(this, serializer);
            RemoveEqualProperties(objTarget, objIgnore);
            return objTarget.ToString();
        }

        public Config OverwriteFromJson(string jsonText)
        { // 이미 instance 위에 덮어쓰기(population) 위해
            serializer.Populate(new StringReader(jsonText), this);
            return this;
        }

        private readonly JsonSerializer serializer;

        #region helpers
        private string Serialize(object? obj)
        {
            var sw = new StringWriter();
            serializer.Serialize(sw, obj);
            return sw.ToString();
        }

        private static void RemoveEqualProperties(JToken? dst, JToken? src)
        {
            // https://github.com/JamesNK/Newtonsoft.Json/issues/2613
            // https://stackoverflow.com/questions/33022993
            if (src == null || dst == null) return;
            if (dst.Type != src.Type) return;

            var props = dst
                .OfType<JProperty>();
            var removables = new List<JProperty>();
            foreach (var jprop in props)
            {
                if (jprop.HasValues == false) continue;

                var dstValue = jprop.Value;
                var srcValue = src[jprop.Name];

                if (dstValue?.ToString() == srcValue?.ToString())
                {
                    removables.Add(jprop);
                }
                RemoveEqualProperties(dstValue, srcValue);
            }
            foreach (var prop in removables)
                prop.Remove();
        }
        #endregion
    }
}