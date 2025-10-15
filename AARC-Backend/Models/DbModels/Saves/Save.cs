using System.ComponentModel.DataAnnotations;

namespace AARC.Models.DbModels.Saves
{
    public class Save : IDbModel
    {
        public int Id { get; set; }
        [MaxLength(nameMaxLength)]
        public required string Name { get; set; }
        [MaxLength(versionMaxLength)]
        public string? Version { get; set; }
        public int OwnerUserId { get; set; }
        [MaxLength(introMaxLength)]
        public string? Intro { get; set; }
        public string? Data { get; set; }
        public int StaCount { get; set; }
        public int LineCount { get; set; }
        public byte Priority { get; set; }
        public DateTime LastActive { get; set; }
        public bool Deleted { get; set; }

        // 新增：存储是否公开（对外可见）
        // 默认为 true，兼容老数据（需要在 DB 端添加列并设置默认值）
        public bool IsVisible { get; set; } = true;

        public const int nameMaxLength = 64;
        public const int versionMaxLength = 32;
        public const int introMaxLength = 256;
    }
}