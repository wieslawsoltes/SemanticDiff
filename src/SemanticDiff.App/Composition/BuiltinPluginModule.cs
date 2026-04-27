using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Git;
using SemanticDiff.Layout;
using SemanticDiff.Semantics.Roslyn;
using SemanticDiff.Semantics.Xaml;

namespace SemanticDiff.App.Composition;

public sealed class BuiltinPluginModule : IPluginModule
{
    public string Id => "semanticdiff.builtin";

    public void Register(IPluginRegistry registry)
    {
        registry.Add<IDiffDocumentFactory>(new DiffDocumentFactory());
        registry.Add<IDocumentTokenizer>(new TextMateDocumentTokenizer());
        registry.Add<IGitRepositoryDiscovery>(new GitRepositoryDiscovery());
        registry.Add<IGitDiffService>(new GitDiffService());
        registry.Add<IGitDiffDocumentService>(new GitDiffDocumentService());
        registry.Add<IGitHistoryService>(new GitHistoryService());
        registry.Add<IGitReviewService>(new GitReviewService());
        registry.Add<IGitReviewDiscussionService>(new GitReviewDiscussionService());
        registry.Add<IGitBlameService>(new GitBlameService());
        registry.Add<ISemanticProvider>(new CSharpSemanticProvider());
        registry.Add<ISemanticProvider>(new XamlSemanticProvider());
        registry.Add<IGraphLayoutEngine>(new MsaglGraphLayoutEngine());
    }
}
