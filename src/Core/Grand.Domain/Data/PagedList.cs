﻿using MongoDB.Driver;
using MongoDB.Driver.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Grand.Domain
{
    public partial class PagedList<T> : List<T>, IPagedList<T>
    {
        private async Task InitializeAsync(IMongoQueryable<T> source, int pageIndex, int pageSize, int? totalCount = null)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (pageSize <= 0)
                pageSize = 1;

            TotalCount = totalCount ?? await source.CountAsync();
            source = totalCount == null ? source.Skip(pageIndex * pageSize).Take(pageSize) : source;
            AddRange(source);

            if (pageSize > 0)
            {
                TotalPages = TotalCount / pageSize;
                if (TotalCount % pageSize > 0)
                    TotalPages++;
            }

            PageSize = pageSize;
            PageIndex = pageIndex;
        }

        private async Task InitializeAsync(IMongoCollection<T> source, FilterDefinition<T> filterdefinition, SortDefinition<T> sortdefinition, int pageIndex, int pageSize, FindOptions findOptions = null)
        {
            TotalCount = (int)await source.CountDocumentsAsync(filterdefinition);

            AddRange(await source.Find(filterdefinition, findOptions).Sort(sortdefinition).Skip(pageIndex * pageSize).Limit(pageSize).ToListAsync());
            if (pageSize > 0)
            {
                TotalPages = TotalCount / pageSize;
                if (TotalCount % pageSize > 0)
                    TotalPages++;
            }
            PageSize = pageSize;
            PageIndex = pageIndex;
        }

        private async Task InitializeAsync(IAggregateFluent<T> source, int pageIndex, int pageSize)
        {
            var range = source.Skip(pageIndex * pageSize).Limit(pageSize + 1).ToList();
            int total = range.Count > pageSize ? range.Count : pageSize;
            TotalCount = (await source.ToListAsync()).Count;
            if (pageSize > 0)
                TotalPages = total / pageSize;

            if (total % pageSize > 0)
                TotalPages++;

            PageSize = pageSize;
            PageIndex = pageIndex;
            AddRange(range.Take(pageSize));
        }

        public static async Task<PagedList<T>> Create(IMongoCollection<T> source, FilterDefinition<T> filterdefinition, SortDefinition<T> sortdefinition, int pageIndex, int pageSize, FindOptions findOptions = null)
        {
            var pagelist = new PagedList<T>();
            await pagelist.InitializeAsync(source, filterdefinition, sortdefinition, pageIndex, pageSize, findOptions);
            return pagelist;
        }
        public static async Task<PagedList<T>> Create(IQueryable<T> source, FilterDefinition<T> filterdefinition, SortDefinition<T> sortdefinition, int pageIndex, int pageSize, FindOptions findOptions = null)
        {
            var pagelist = new PagedList<T>();
            await pagelist.InitializeAsync((IMongoCollection<T>)source, filterdefinition, sortdefinition, pageIndex, pageSize, findOptions);
            return pagelist;
        }

        public static async Task<PagedList<T>> Create(IQueryable<T> source, int pageIndex, int pageSize, FindOptions findOptions = null)
        {
            var pagelist = new PagedList<T>();
            await pagelist.InitializeAsync((IMongoQueryable<T>)source, pageIndex, pageSize);
            return pagelist;
        }

        public static async Task<PagedList<T>> Create(IMongoQueryable<T> source, int pageIndex, int pageSize)
        {
            var pagelist = new PagedList<T>();
            await pagelist.InitializeAsync(source, pageIndex, pageSize);
            return pagelist;
        }
        public static async Task<PagedList<T>> Create(IAggregateFluent<T> source, int pageIndex, int pageSize)
        {
            var pagelist = new PagedList<T>();
            await pagelist.InitializeAsync(source, pageIndex, pageSize);
            return pagelist;
        }
    }
}