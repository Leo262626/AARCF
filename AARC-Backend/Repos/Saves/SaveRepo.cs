using AARC.Models.Db.Context;
using AARC.Models.Db.Context.Specific;
using AARC.Models.DbModels.Identities;
using AARC.Models.DbModels.Saves;
using AARC.Services.App.HttpAuthInfo;
using AARC.Services.App.Mapping;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;

namespace AARC.Repos.Saves
{
    public class SaveRepo(
        AarcContext context,
        HttpUserIdProvider httpUserIdProvider,
        HttpUserInfoService httpUserInfoService,
        IMapper mapper
        ) : Repo<Save>(context)
    {
        private IQueryable<Save> GetOwnerTypedSaves(bool isTourist = false)
        {
            var userQ = base.Context.Users.Existing();
            if (isTourist)
                userQ = userQ.Where(x => x.Type == UserType.Tourist);
            else
                userQ = userQ.Where(x => x.Type > UserType.Tourist);
            var filteredByUserType =
                from u in userQ
                join s in base.Existing
                on u.Id equals s.OwnerUserId
                select s;
            return filteredByUserType;
        }

        private IQueryable<Save> Viewable
        {
            get
            {
                if (httpUserInfoService.IsAdmin)
                    return Existing; // 管理员可查看所有

                var uid = httpUserIdProvider.UserIdLazy.Value;

                // 先取非游客（或按需求的 isTourist 参数得到的类型）的作品集
                var res = GetOwnerTypedSaves(isTourist: false);

                // 非管理员只能看到公开的作品，或者是自己的作品（owner）
                res = res.Where(x => x.IsVisible || x.OwnerUserId == uid);

                // 如果请求者是游客，还应把自己的作品也加上（以便游客看到自己的未公开作品）
                if (!httpUserInfoService.IsTourist)
                    return res;

                if (uid > 0)
                {
                    var mine = Existing.Where(x => x.OwnerUserId == uid);
                    res = res.Union(mine);
                }

                return res;
            }
        }

        public List<SaveDto> GetNewestSaves(bool forAuditor)
        {
            var res = GetOwnerTypedSaves(isTourist: forAuditor)
                .Where(x => x.IsVisible) // newest list for public view should be public only
                .OrderByDescending(x => x.LastActive)
                .ProjectTo<SaveDto>(mapper.ConfigurationProvider)
                .Take(10)
                .ToList();
            return res;
        }

        public List<SaveDto> GetMySaves(int uid = 0)
        {
            bool isSelf = false;
            if (uid == 0) // 如未提供目标uid，则解析为查看自己的
            {
                uid = httpUserIdProvider.UserIdLazy.Value;
                isSelf = true;
            }
            if (uid == 0) // 如自己的uid仍为0，则要登录
                throw new RqEx(null, System.Net.HttpStatusCode.Unauthorized);

            if (!httpUserInfoService.IsAdmin && !isSelf)
            {
                // 如果请求者不是管理员也不是目标用户，需要确保目标用户不是游客
                UserType ownerType = Context.Users
                    .Where(x => x.Id == uid)
                    .Select(x => x.Type)
                    .FirstOrDefault();
                if (ownerType == UserType.Tourist)
                    throw new RqEx("无权查看");
            }

            var res = base.Existing
                .Where(x => x.OwnerUserId == uid)
                .OrderByDescending(x => x.LastActive)
                .ProjectTo<SaveDto>(mapper.ConfigurationProvider)
                .ToList();
            return res;
        }

        public List<SaveDto> Search(
            string search, string orderby, int pageIdx)
        {
            var q = Viewable;

            //sqlite默认大小写不敏感，此处强制转小写处理
            if (Context is AarcSqliteContext)
                q = q.Where(x => x.Name.ToLower().Contains(search.ToLower()));
            else
                q = q.Where(x => x.Name.Contains(search));
            if (orderby == "sta")
            {
                q = q
                    .OrderByDescending(q => q.StaCount)
                    .ThenByDescending(q => q.LastActive);
            }
            else
                q = q
                    .OrderByDescending(x => x.LastActive);
            int pageSize = 50;
            int skip = pageIdx * pageSize;
            int take = pageSize;
            var res = q
                .ProjectTo<SaveDto>(mapper.ConfigurationProvider)
                .Skip(skip)
                .Take(take)
                .ToList();
            return res;
        }

        public bool Create(SaveDto saveDto, out string? errmsg)
        {
            errmsg = ValidateDto(saveDto);
            if (errmsg is { })
                return false;
            var uid = httpUserIdProvider.RequireUserId();
            Save save = mapper.Map<Save>(saveDto);
            save.OwnerUserId = uid;

            // 游客无法创建公开作品：若当前用户是游客并且不是管理员，强制 IsVisible = false
            if (!httpUserInfoService.IsAdmin)
            {
                var myType = Context.Users
                    .Where(x => x.Id == uid)
                    .Select(x => x.Type)
                    .FirstOrDefault();
                if (myType == UserType.Tourist)
                    save.IsVisible = false;
            }

            base.Add(save);
            return true;
        }

        public bool UpdateInfo(SaveDto saveDto, out string? errmsg)
        {
            errmsg = ValidateDto(saveDto);
            if (errmsg is { }) return false;
            errmsg = ValidateAccess(saveDto.Id);
            if (errmsg is { }) return false;

            // 如果要把作品设为公开，确保作品所属用户不是游客（或请求者是管理员）
            if (saveDto.IsVisible)
            {
                var ownerId = base.WithId(saveDto.Id).Select(x => x.OwnerUserId).FirstOrDefault();
                var ownerType = Context.Users
                    .Where(x => x.Id == ownerId)
                    .Select(x => x.Type)
                    .FirstOrDefault();
                if (ownerType == UserType.Tourist && !httpUserInfoService.IsAdmin)
                {
                    errmsg = "无权将游客作品设为公开";
                    return false;
                }
            }

            var updated = Existing
                .Where(x => x.Id == saveDto.Id)
                .ExecuteUpdate(spc => spc
                    .SetProperty(x => x.Name, saveDto.Name)
                    .SetProperty(x => x.Version, saveDto.Version)
                    .SetProperty(x => x.Intro, saveDto.Intro)
                    .SetProperty(x => x.IsVisible, saveDto.IsVisible)); // 写入 IsVisible

            if (updated == 0)
            {
                errmsg = "找不到该存档";
                return false;
            }
            return true;
        }

        public bool UpdateData(
            int id, string data,
            int staCount, int lineCount, out string? errmsg)
        {
            errmsg = ValidateAccess(id);
            if (errmsg is { }) return false;
            var originalLength = Existing
                .Where(x => x.Id == id && x.Data != null)
                .Select(x => x.Data!.Length)
                .FirstOrDefault();
            if (originalLength > 1000)
            {
                if (data.Length < originalLength / 4)
                {
                    errmsg = "内容显著减少，拒绝保存";
                    return false;
                }
            }

            var updated = Existing
                .Where(x => x.Id == id)
                .ExecuteUpdate(spc => spc
                    .SetProperty(x => x.LastActive, DateTime.Now)
                    .SetProperty(x => x.Data, data)
                    .SetProperty(x => x.StaCount, staCount)
                    .SetProperty(x => x.LineCount, lineCount));
            if (updated == 0)
            {
                errmsg = "找不到该存档";
                return false;
            }
            errmsg = null;
            return true;
        }

        public SaveDto? LoadInfo(int id, out string? errmsg)
        {
            var res = Viewable
                .Where(x => x.Id == id)
                .ProjectTo<SaveDto>(mapper.ConfigurationProvider)
                .FirstOrDefault();
            if (res is null)
            {
                errmsg = "无法加载存档信息";
                return null;
            }
            errmsg = null;
            return res;
        }

        public string? LoadData(int id, out string? errmsg)
        {
            var res = Viewable
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.Data })
                .FirstOrDefault();
            if (res is null)
            {
                errmsg = "无法加载存档数据";
                return null;
            }
            errmsg = null;
            return res.Data;
        }

        public bool Remove(int id, out string? errmsg)
        {
            errmsg = ValidateAccess(id);
            if (errmsg is { }) return false;
            base.FakeRemove(id);
            errmsg = null;
            return true;
        }

        private static string? ValidateDto(SaveDto saveDto)
        {
            if (string.IsNullOrWhiteSpace(saveDto.Name))
                return "名称不能为空";
            if (saveDto.Name.Length < 1 || saveDto.Name.Length > Save.nameMaxLength)
                return $"名称长度必须为2-{Save.nameMaxLength}字符";
            if (saveDto.Version?.Length > Save.versionMaxLength)
                return $"版本长度必须小于{Save.versionMaxLength}字符";
            if (saveDto.Intro?.Length > Save.introMaxLength)
                return $"简介长度必须小于{Save.introMaxLength}字符";
            return null;
        }

        private string? ValidateAccess(int saveId)
        {
            var ownerId = base.WithId(saveId).Select(x => x.OwnerUserId).FirstOrDefault();
            var uinfo = httpUserInfoService.UserInfo.Value;
            if (uinfo is null)
                return "请登录";
            if (uinfo.Id != ownerId && !uinfo.IsAdmin)
                return "无权编辑本存档";
            return null;
        }
    }

    public class SaveDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? MiniUrl { get; set; }
        public string? Version { get; set; }
        public int OwnerUserId { get; set; }
        public string? OwnerName { get; set; }
        public string? Intro { get; set; }
        public int StaCount { get; set; }
        public int LineCount { get; set; }
        public byte Priority { get; set; }
        public string? LastActive { get; set; }

        // 新增 DTO 字段：是否公开（会被前端生成的 apiGenerated.ts 映射为 isVisible）
        public bool IsVisible { get; set; } = true;
    }

    public class SaveDtoProfile : Profile
    {
        public SaveDtoProfile()
        {
            CreateMap<SaveDto, Save>()
                .IgnoreLastActive();

            CreateMap<Save, SaveDto>()
                .ForMember(
                    destinationMember: x => x.LastActive,
                    memberOptions: mem => mem.MapFrom(source => source.LastActive.ToString("yyyy-MM-dd HH:mm")))
                // IsVisible 名称一致，AutoMapper 会自动映射；此处列出以示意
                ;
        }
    }
}