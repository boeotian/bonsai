﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Bonsai.Areas.Admin.Logic.Validation;
using Bonsai.Areas.Admin.ViewModels.Dashboard;
using Bonsai.Areas.Admin.ViewModels.Pages;
using Bonsai.Code.Utils.Helpers;
using Bonsai.Code.Utils.Validation;
using Bonsai.Data;
using Bonsai.Data.Models;
using Impworks.Utils.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Bonsai.Areas.Admin.Logic
{
    /// <summary>
    /// The manager service for handling pages.
    /// </summary>
    public class PagesManagerService
    {
        public PagesManagerService(AppDbContext db, IMapper mapper, UserManager<AppUser> userMgr, PageValidator validator)
        {
            _db = db;
            _mapper = mapper;
            _userMgr = userMgr;
            _validator = validator;
        }

        private readonly AppDbContext _db;
        private readonly IMapper _mapper;
        private readonly UserManager<AppUser> _userMgr;
        private readonly PageValidator _validator;

        #region Public methods

        /// <summary>
        /// Finds pages.
        /// </summary>
        public async Task<PagesListVM> GetPagesAsync(PagesListRequestVM request)
        {
            const int PageSize = 20;

            request = NormalizeListRequest(request);

            var query = _db.Pages.Include(x => x.MainPhoto).AsQueryable();

            if(!string.IsNullOrEmpty(request.SearchQuery))
                query = query.Where(x => x.Title.ToLower().Contains(request.SearchQuery.ToLower()));

            if (request.Types?.Length > 0)
                query = query.Where(x => request.Types.Contains(x.Type));

            var totalCount = await query.CountAsync();

            var items = await query.Where(x => x.IsDeleted == false)
                                   .OrderBy(request.OrderBy, request.OrderDescending)
                                   .ProjectTo<PageTitleExtendedVM>()
                                   .Skip(PageSize * request.Page)
                                   .Take(PageSize)
                                   .ToListAsync();

            return new PagesListVM
            {
                Items = items,
                PageCount = (int) Math.Ceiling((double) totalCount / PageSize),
                Request = request
            };
        }

        /// <summary>
        /// Returns the lookup of page titles.
        /// </summary>
        public async Task<IReadOnlyDictionary<Guid, PageTitleExtendedVM>> FindPagesByIdsAsync(IReadOnlyList<Guid?> pages)
        {
            return await _db.Pages
                            .Include(x => x.MainPhoto)
                            .Where(x => pages.Any(y => x.Id == y) && x.IsDeleted == false)
                            .ProjectTo<PageTitleExtendedVM>()
                            .ToDictionaryAsync(x => x.Id, x => x);
        }
        
        /// <summary>
        /// Creates the new page.
        /// </summary>
        public async Task<Page> CreateAsync(PageEditorVM vm, ClaimsPrincipal principal)
        {
            await ValidateRequestAsync(vm);

            var page = _mapper.Map<Page>(vm);
            page.Id = Guid.NewGuid();
            page.CreationDate = DateTimeOffset.Now;
            page.MainPhoto = await FindMainPhotoAsync(vm.MainPhotoKey);

            await _validator.ValidateAsync(page, vm.Facts);

            var changeset = await GetChangesetAsync(null, vm, page.Id, principal);
            _db.Changes.Add(changeset);

            _db.Pages.Add(page);
            _db.PageAliases.Add(new PageAlias
            {
                Id = Guid.NewGuid(),
                Page = page,
                Key = page.Key,
                Title = page.Title
            });

            return page;
        }

        /// <summary>
        /// Returns the original data for the editor form.
        /// </summary>
        public async Task<PageEditorVM> RequestUpdateAsync(Guid id)
        {
            var page = await _db.Pages
                                .AsNoTracking()
                                .Include(x => x.MainPhoto)
                                .Include(x => x.Aliases)
                                .GetAsync(x => x.Id == id && x.IsDeleted == false, "Страница не найдена");

            return _mapper.Map<Page, PageEditorVM>(page);
        }

        /// <summary>
        /// Updates the changes to a page.
        /// </summary>
        public async Task<Page> UpdateAsync(PageEditorVM vm, ClaimsPrincipal principal)
        {
            await ValidateRequestAsync(vm);

            var page = await _db.Pages
                                .Include(x => x.Aliases)
                                .Include(x => x.MainPhoto)
                                .GetAsync(x => x.Id == vm.Id && x.IsDeleted == false, "Страница не найдена");

            await _validator.ValidateAsync(page, vm.Facts);

            var changeset = await GetChangesetAsync(_mapper.Map<PageEditorVM>(page), vm, vm.Id, principal);
            _db.Changes.Add(changeset);

            _mapper.Map(vm, page);
            page.MainPhotoId = (await FindMainPhotoAsync(vm.MainPhotoKey))?.Id;

            await _db.PageAliases.RemoveWhereAsync(x => x.Page.Id == vm.Id);

            var aliasValues = JsonConvert.DeserializeObject<List<string>>(vm.Aliases ?? "[]");
            if(!aliasValues.Contains(vm.Title))
                aliasValues.Add(vm.Title);

            _db.PageAliases.AddRange(
                aliasValues.Select((x, idx) => new PageAlias
                {
                    Id = Guid.NewGuid(),
                    Key = PageHelper.EncodeTitle(x),
                    Page = page,
                    Order = idx,
                    Title = x
                })
            );

            return page;
        }

        /// <summary>
        /// Displays the remove confirmation form.
        /// </summary>
        public async Task<PageTitleExtendedVM> RequestRemoveAsync(Guid id)
        {
            return await _db.Pages
                            .Where(x => x.IsDeleted)
                            .ProjectTo<PageTitleExtendedVM>()
                            .GetAsync(x => x.Id == id, "Страница не найдена");
        }

        /// <summary>
        /// Removes the page.
        /// </summary>
        public async Task<Page> RemoveAsync(Guid id, ClaimsPrincipal principal)
        {
            var page = await _db.Pages
                                .GetAsync(x => x.Id == id && x.IsDeleted == false, "Страница не найдена");

            var prev = await RequestUpdateAsync(id);
            var changeset = await GetChangesetAsync(prev, null, id, principal);
            _db.Changes.Add(changeset);

            page.IsDeleted = true;

            return page;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Completes and\or corrects the search request.
        /// </summary>
        private PagesListRequestVM NormalizeListRequest(PagesListRequestVM vm)
        {
            if (vm == null)
                vm = new PagesListRequestVM();

            var orderableFields = new[] {nameof(Page.Title), nameof(Page.LastUpdateDate), nameof(Page.CreationDate)};
            if (!orderableFields.Contains(vm.OrderBy))
                vm.OrderBy = orderableFields[0];

            if (vm.Page < 0)
                vm.Page = 0;

            return vm;
        }

        /// <summary>
        /// Checks if the create/update request contains valid data.
        /// </summary>
        private async Task ValidateRequestAsync(PageEditorVM vm)
        {
            var val = new Validator();

            var key = PageHelper.EncodeTitle(vm.Title);
            var otherPage = await _db.PageAliases
                                     .AnyAsync(x => x.Key == key && x.Page.Id != vm.Id);

            if (otherPage)
                val.Add(nameof(PageEditorVM.Title), "Страница с таким названием уже существует.");

            if (!IsDeserializable<string[]>(vm.Aliases))
                val.Add(nameof(PageEditorVM.Aliases), "Псевдонимы указаны неверно!");

            val.ThrowIfInvalid();
        }

        /// <summary>
        /// Gets the changeset for updates.
        /// </summary>
        private async Task<Changeset> GetChangesetAsync(PageEditorVM prev, PageEditorVM next, Guid id, ClaimsPrincipal principal)
        {
            if(prev == null && next == null)
                throw new ArgumentNullException();

            var userId = _userMgr.GetUserId(principal);
            var user = await _db.Users.GetAsync(x => x.Id == userId, "Пользователь не найден");

            return new Changeset
            {
                Id = Guid.NewGuid(),
                Type = ChangesetEntityType.Page,
                Date = DateTime.Now,
                EditedPageId = id,
                Author = user,
                OriginalState = prev == null ? null : JsonConvert.SerializeObject(prev),
                UpdatedState = next == null ? null : JsonConvert.SerializeObject(next),
            };
        }

        /// <summary>
        /// Finds the image to use for the page.
        /// </summary>
        private async Task<Media> FindMainPhotoAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            var media = await _db.Media
                                 .FirstOrDefaultAsync(x => x.Key == key && x.IsDeleted == false);

            if(media == null)
                throw new ValidationException(nameof(PageEditorVM.MainPhotoKey), "Фотография не найдена!");

            if(media.Type != MediaType.Photo)
                throw new ValidationException(nameof(PageEditorVM.MainPhotoKey), "Медиа-файл не является фотографией!");

            return media;
        }

        /// <summary>
        /// Checks if the serialized field contains valid data.
        /// </summary>
        private bool IsDeserializable<T>(string value)
        {
            if (string.IsNullOrEmpty(value))
                return true;

            try
            {
                JsonConvert.DeserializeObject<T>(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}

