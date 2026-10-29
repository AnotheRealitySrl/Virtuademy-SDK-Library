using System;

using UnityEngine;

namespace Virtuademy.SDK.ApiData
{
    [Serializable]
    [Newtonsoft.Json.JsonObject(Newtonsoft.Json.MemberSerialization.Fields)]
    public class EnvironmentDTO
    {
        [SerializeField] private int id;
        [SerializeField] private DateTime creationDate;
        [SerializeField] private DateTime lastUpdate;
        [SerializeField] private string label;
        [SerializeField] private string fileName;
        [SerializeField] private string thumbnailUri;
        [SerializeField] private string localizationUri;
        [SerializeField] private CatalogDTO catalogInfo;
        [SerializeField] private string description;
        [SerializeField] private EStatusOption status;
        [SerializeField] private int worldId;
        [SerializeField] private bool isTenant;
        [SerializeField] private int ownerUserId;
        [SerializeField] private string[] platforms;
        [SerializeField] private TagDTO[] tags = new TagDTO[0];
        [SerializeField] private bool multiplayer;
        [SerializeField] private string format;

        public int Id => id;
        public DateTime CreationDate => creationDate;
        public DateTime LastUpdate => lastUpdate;
        public string Label => label;
        public string FileName => fileName;
        public CatalogDTO Catalog => catalogInfo;
        public string Description => description;
        public EStatusOption Status => status;
        public int WorldId => worldId;
        public bool IsTenant => isTenant;
        public int OwnerUserId => ownerUserId;
        public string[] Platforms => platforms;
        public string ThumbnailUri => thumbnailUri;
        public string LocalizationUri => localizationUri;
        public TagDTO[] Tags => tags;
        [Obsolete("Use SessionDTO.Multiplayer for a session, WorldDTO.Multiplayer for a world.")]
        public bool Multiplayer { get => multiplayer; set => multiplayer = value; }
        /// <summary>
        /// What the environment is made of, as the Application API sends it (<c>UnityAddressable</c>, ...).
        /// A string on purpose, so a value this client does not know survives to be reported; null when
        /// the API does not send the field.
        /// </summary>
        public string Format => format;
    }
}