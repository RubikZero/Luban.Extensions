namespace Luban.MultiRootPathValidator;

internal interface IPathPattern
{
    bool ExistPath(string rootDir, string subFile);

    bool EmptyAble { get; set; }
}
