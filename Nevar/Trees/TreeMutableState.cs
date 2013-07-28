﻿using Nevar.Impl.FileHeaders;

namespace Nevar.Trees
{
    public unsafe class TreeMutableState
    {
        public long BranchPages;
        public long LeafPages;
        public long OverflowPages;
        public int Depth;
        public long PageCount;
        public long EntriesCount;

        public Page Root;


        public void CopyTo(TreeRootHeader* header)
        {
            header->BranchPages = BranchPages;
            header->Depth = Depth;
            header->Flags = TreeFlags.None;
            header->LeafPages = LeafPages;
            header->OverflowPages = OverflowPages;
            header->PageCount = PageCount;
            header->EntriesCount = EntriesCount;
            header->RootPageNumber = Root.PageNumber;
        }

        public TreeMutableState Clone()
        {
            return new TreeMutableState
                {
                    BranchPages = BranchPages,
                    Depth = Depth,
                    EntriesCount = EntriesCount,
                    LeafPages = LeafPages,
                    OverflowPages = OverflowPages,
                    PageCount = PageCount,
                    Root = new Page(Root.Base, Root.PageMaxSpace)
                };
        }
    }
}