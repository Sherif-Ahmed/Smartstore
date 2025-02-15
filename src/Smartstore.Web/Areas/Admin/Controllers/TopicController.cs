﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dasync.Collections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Smartstore.Admin.Models.Topics;
using Smartstore.Collections;
using Smartstore.ComponentModel;
using Smartstore.Core.Catalog.Brands;
using Smartstore.Core.Catalog.Categories;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Content.Menus;
using Smartstore.Core.Content.Topics;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Security;
using Smartstore.Core.Seo;
using Smartstore.Core.Stores;
using Smartstore.Web.Controllers;
using Smartstore.Web.Modelling;
using Smartstore.Web.Modelling.DataGrid;

namespace Smartstore.Admin.Controllers
{
    public class TopicController : AdminControllerBase
    {
        private readonly SmartDbContext _db;
        private readonly ILocalizedEntityService _localizedEntityService;
        private readonly IStoreMappingService _storeMappingService;
        private readonly IAclService _aclService;
        private readonly ILinkResolver _linkResolver;
        private readonly IUrlService _urlService;
        private readonly IStoreContext _storeContext;

        public TopicController(
            SmartDbContext db,
            ILocalizedEntityService localizedEntityService,
            IStoreMappingService storeMappingService,
            IAclService aclService,
            ILinkResolver linkResolver,
            IUrlService urlService,
            IStoreContext storeContext)
        {
            _db = db;
            _localizedEntityService = localizedEntityService;
            _storeMappingService = storeMappingService;
            _aclService = aclService;
            _linkResolver = linkResolver;
            _urlService = urlService;
            _storeContext = storeContext;
        }

        public IActionResult Index()
        {
            return RedirectToAction("List");
        }

        [Permission(Permissions.Cms.Topic.Read)]
        public IActionResult List()
        {
            var model = new TopicListModel
            {
                IsSingleStoreMode = _storeContext.IsSingleStoreMode()
            };

            return View(model);
        }

        [HttpPost]
        [Permission(Permissions.Cms.Topic.Read)]
        public async Task<IActionResult> TopicList(GridCommand command, TopicListModel model)
        {
            var query = _db.Topics
                .ApplyStoreFilter(model.SearchStoreId)
                .AsNoTracking();

            if (model.SystemName.HasValue())
            {
                query = query.Where(x => x.SystemName.Contains(model.SystemName));
            }
            
            if (model.Title.HasValue())
            {
                query = query.Where(x => x.Title.Contains(model.Title) || x.ShortTitle.Contains(model.Title));
            }

            if (model.RenderAsWidget.HasValue)
            {
                query = query.Where(x => x.RenderAsWidget == model.RenderAsWidget.Value);
            }

            if (model.WidgetZone.HasValue())
            {
                query = query.Where(x => x.WidgetZone.Contains(model.WidgetZone));
            }

            query = query.ApplyGridCommand(command, false);

            var topicItems = await query.ToPagedList(command.Page - 1, command.PageSize).LoadAsync();
            var gridModel = new GridModel<TopicModel>
            {
                Rows = await topicItems.AsEnumerable().SelectAsync(async x => await PrepareTopicListModelAsync(x)).AsyncToList(),
                Total = topicItems.TotalCount
            };

            return Json(gridModel);
        }

        private async Task<TopicModel> PrepareTopicListModelAsync(Topic topic)
        {
            var model = new TopicModel();
            await MapperFactory.MapAsync(topic, model);
            await PrepareTopicModelAsync(topic, model);

            // TODO: (mh) (core) Maybe we need a DisplayTemplate for this.
            model.WidgetZoneValue = topic.WidgetZone;
            model.CookieType = (int?)topic.CookieType;
            model.Body = string.Empty;                          // Otherwise maxJsonLength could be exceeded.
            model.Intro = string.Empty;                          // Otherwise grind may slow down
            model.ViewUrl = Url.Action("Edit", "Topic", new { id = topic.Id });

            return model;
        }

        [Permission(Permissions.Cms.Topic.Create)]
        public IActionResult Create()
        {
            var model = new TopicModel();

            AddLocales(model.Locales);
            AddCookieTypes(model);

            model.TitleTag = "h1";

            return View(model);
        }

        [HttpPost, ParameterBasedOnFormName("save-continue", "continueEditing")]
        [Permission(Permissions.Cms.Topic.Create)]
        public async Task<IActionResult> Create(TopicModel model, bool continueEditing)
        {
            if (ModelState.IsValid)
            {
                if (!model.IsPasswordProtected)
                {
                    model.Password = null;
                }

                var topic = await MapperFactory.MapAsync<TopicModel, Topic>(model);

                if (model.WidgetZone != null)
                {
                    topic.WidgetZone = string.Join(',', model.WidgetZone);
                }

                topic.CookieType = (CookieType?)model.CookieType;

                _db.Topics.Add(topic);
                await _db.SaveChangesAsync();

                var slugResult = await topic.ValidateSlugAsync(model.SeName, true);
                model.SeName = slugResult.Slug;
                await _urlService.ApplySlugAsync(slugResult, true);

                await SaveStoreMappingsAsync(topic, model.SelectedStoreIds);
                await SaveAclMappingsAsync(topic, model.SelectedCustomerRoleIds);
                await UpdateLocalesAsync(topic, model);
                AddCookieTypes(model, model.CookieType);

                await Services.EventPublisher.PublishAsync(new ModelBoundEvent(model, topic, Request.Form));

                NotifySuccess(T("Admin.ContentManagement.Topics.Updated"));
                return continueEditing ? RedirectToAction("Edit", new { id = topic.Id }) : RedirectToAction("List");
            }

            // If we got this far something failed. Redisplay form.
            return View(model);
        }

        [Permission(Permissions.Cms.Topic.Read)]
        public async Task<IActionResult> Edit(int id)
        {
            var topic = await _db.Topics.FindByIdAsync(id, false);
            if (topic == null)
            {
                return RedirectToAction("List");
            }

            var model = await MapperFactory.MapAsync<Topic, TopicModel>(topic);
            await PrepareTopicModelAsync(topic, model);

            model.WidgetZone = topic.WidgetZone.SplitSafe(",").ToArray();
            model.CookieType = (int?)topic.CookieType;

            AddLocales(model.Locales, async (locale, languageId) =>
            {
                locale.ShortTitle = topic.GetLocalized(x => x.ShortTitle, languageId, false, false);
                locale.Title = topic.GetLocalized(x => x.Title, languageId, false, false);
                locale.Intro = topic.GetLocalized(x => x.Intro, languageId, false, false);
                locale.Body = topic.GetLocalized(x => x.Body, languageId, false, false);
                locale.MetaKeywords = topic.GetLocalized(x => x.MetaKeywords, languageId, false, false);
                locale.MetaDescription = topic.GetLocalized(x => x.MetaDescription, languageId, false, false);
                locale.MetaTitle = topic.GetLocalized(x => x.MetaTitle, languageId, false, false);
                locale.SeName = await topic.GetActiveSlugAsync(languageId, false, false);
            });

            // Get menu links.
            IPagedList<MenuEntity> menus = null;
            var pageIndex = 0;

            do
            {
                menus = await _db.Menus
                    .ApplyStandardFilter(true)
                    .ToPagedList(pageIndex++, 500)
                    .LoadAsync();
                
                foreach (var menu in menus)
                {
                    foreach (var item in menu.Items.Where(x => x.ProviderName != null && x.ProviderName == "entity"))
                    {
                        var link = await _linkResolver.ResolveAsync(item.Model);
                        if (link.Type == LinkType.Topic && link.Id == topic.Id)
                        {
                            var url = Url.Action("EditItem", "Menu", new { id = item.Id, area = "Admin" });

                            var label = string.Concat(
                                menu.Title.NullEmpty() ?? menu.SystemName.NullEmpty() ?? "".NaIfEmpty(),
                                " » ",
                                item.Title.NullEmpty() ?? link.Label.NullEmpty() ?? "".NaIfEmpty());

                            model.MenuLinks[url] = label;
                        }
                    }
                }
            }
            while (menus.HasNextPage);

            return View(model);
        }

        [HttpPost]
        [ParameterBasedOnFormName("save-continue", "continueEditing")]
        [Permission(Permissions.Cms.Topic.Update)]
        public async Task<IActionResult> Edit(TopicModel model, bool continueEditing)
        {
            var topic = await _db.Topics.FindByIdAsync(model.Id);
            if (topic == null)
            {
                return RedirectToAction("List");
            }

            if (!model.IsPasswordProtected)
            {
                model.Password = null;
            }

            if (ModelState.IsValid)
            {
                await MapperFactory.MapAsync(model, topic);

                if (model.WidgetZone != null)
                {
                    topic.WidgetZone = string.Join(',', model.WidgetZone);
                }

                topic.CookieType = (CookieType?)model.CookieType;

                await _db.SaveChangesAsync();

                var slugResult = await topic.ValidateSlugAsync(model.SeName, true);
                model.SeName = slugResult.Slug;
                await _urlService.ApplySlugAsync(slugResult, true);
                
                await SaveStoreMappingsAsync(topic, model.SelectedStoreIds);
                await SaveAclMappingsAsync(topic, model.SelectedCustomerRoleIds);
                await UpdateLocalesAsync(topic, model);
                AddCookieTypes(model, model.CookieType);

                await Services.EventPublisher.PublishAsync(new ModelBoundEvent(model, topic, Request.Form));

                NotifySuccess(T("Admin.ContentManagement.Topics.Updated"));
                return continueEditing ? RedirectToAction("Edit", new { id = topic.Id }) : RedirectToAction("List");
            }

            // If we got this far something failed. Redisplay form.
            await PrepareTopicModelAsync(topic, model);

            return View(model);
        }

        /// <summary>
        /// (ajax) Gets a list of all available topics.
        /// </summary>
        /// <param name="label">Text for optional entry. If not null an entry with the specified label text and the Id 0 will be added to the list.</param>
        /// <param name="selectedIds">Ids of selected entities.</param>
        /// <param name="includeWidgets">Specifies whether to include topics which are defined to be rendered as Widgets.</param>
        /// <param name="includeHomePage">Specifies whether to include homepage text.</param>
        /// <returns>List of all topics as JSON.</returns>
        public async Task<IActionResult> AllTopics(string label, int selectedId, bool includeWidgets = false, bool includeHomePage = false)
        {
            var topics = await _db.Topics
                .AsNoTracking()
                .ApplyStandardFilter(true)
                .Where(x => includeWidgets || !x.RenderAsWidget)
                .ToListAsync();
            
            var list = topics
                .Select(x =>
                {
                    var item = new ChoiceListItem
                    {
                        Id = x.Id.ToString(),
                        Text = x.GetLocalized(y => y.Title).Value.NullEmpty() ?? x.SystemName,
                        Selected = x.Id == selectedId
                    };

                    if (!item.Text.EqualsNoCase(x.SystemName))
                    {
                        item.Description = x.SystemName;
                    }

                    return item;
                })
                .ToList();

            if (label.HasValue())
            {
                list.Insert(0, new ChoiceListItem { Id = "0", Text = label, Selected = false });
            }

            if (includeHomePage)
            {
                list.Insert(0, new ChoiceListItem { Id = "-10", Text = T("Admin.ContentManagement.Homepage").Value, Selected = false });
            }

            return new JsonResult(list);
        }

        #region Helper functions

        private async Task PrepareTopicModelAsync(Topic topic, TopicModel model)
        {
            model.Url = await GetTopicUrlAsync(topic);
            model.SelectedStoreIds = await _storeMappingService.GetAuthorizedStoreIdsAsync(topic);
            model.SelectedCustomerRoleIds = await _aclService.GetAuthorizedCustomerRoleIdsAsync(topic);
            AddCookieTypes(model, model.CookieType);
        }

        private async Task UpdateLocalesAsync(Topic topic, TopicModel model)
        {
            foreach (var localized in model.Locales)
            {
                await _localizedEntityService.ApplyLocalizedValueAsync(topic, x => x.ShortTitle, localized.ShortTitle, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(topic, x => x.Title, localized.Title, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(topic, x => x.Intro, localized.Intro, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(topic, x => x.Body, localized.Body, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(topic, x => x.MetaKeywords, localized.MetaKeywords, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(topic, x => x.MetaDescription, localized.MetaDescription, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(topic, x => x.MetaTitle, localized.MetaTitle, localized.LanguageId);

                await _db.SaveChangesAsync();

                var slugResult = await topic.ValidateSlugAsync(localized.SeName, true);
                model.SeName = slugResult.Slug;
                await _urlService.ApplySlugAsync(slugResult, true);
            }
        }

        private async Task<string> GetTopicUrlAsync(Topic topic)
        {
            string url = null;

            try
            {
                if (topic.LimitedToStores)
                {
                    var storeIds = await _storeMappingService.GetAuthorizedStoreIdsAsync(topic);
                    var currentStoreIsAuthorized = storeIds.Contains(Services.StoreContext.CurrentStore.Id);

                    if (currentStoreIsAuthorized)
                    {
                        var store = Services.StoreContext.GetStoreById(Services.StoreContext.CurrentStore.Id);
                        if (store != null)
                        {
                            var baseUri = new Uri(store.GetHost());
                            url = baseUri.GetLeftPart(UriPartial.Authority) + Url.RouteUrl("Topic", new { SeName = await topic.GetActiveSlugAsync() });
                        }
                    }
                }

                if (url.IsEmpty())
                {
                    url = Url.RouteUrl("Topic", new { SeName = await topic.GetActiveSlugAsync() }, Request.Scheme);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }

            return url;
        }

        private static void AddCookieTypes(TopicModel model, int? selectedType = 0)
        {
            model.AvailableCookieTypes.AddRange(new[] 
            {
                new SelectListItem
                {
                    Text = "Required",
                    Value = ((int)CookieType.Required).ToString(),
                    Selected = CookieType.Required == (CookieType?)selectedType
                },
                new SelectListItem
                {
                    Text = "Analytics",
                    Value = ((int)CookieType.Analytics).ToString(),
                    Selected = CookieType.Analytics == (CookieType?)selectedType
                },
                new SelectListItem
                {
                    Text = "ThirdParty",
                    Value = ((int)CookieType.ThirdParty).ToString(),
                    Selected = CookieType.ThirdParty == (CookieType?)selectedType
                }
            });
        }

        #endregion
    }
}
