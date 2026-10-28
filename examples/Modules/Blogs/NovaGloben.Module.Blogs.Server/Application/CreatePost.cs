using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.SharedKernel.Domain;
using NovaGloben.Module.Blogs.Domain;
using NovaGloben.Module.Blogs.Persistence;
using WireModel = NovaGloben.Module.Blogs.Models.BlogPostModel;

namespace NovaGloben.Module.Blogs.Application
{
    /// <summary>
    /// CQRS COMMAND: publish a blog post. Dispatched through <c>IMediator.Send</c> — never called directly — so
    /// it flows through the host's mediator behavior pipeline (exception → logging → performance → validation).
    /// </summary>
    public sealed record CreatePostCommand(int ModuleId, string Title, string Body, string Author)
        : ICommand<WireModel>;

    /// <summary>
    /// The command handler. It builds the <see cref="BlogPost"/> aggregate through its <c>Publish</c> factory
    /// (which enforces every invariant and raises the domain event), persists it through the STORAGE-BLIND
    /// clean-arch repository, drains the unit of work — which dispatches the
    /// <see cref="BlogPostPublishedDomainEvent"/> to the framework dispatcher in this scope — and then PUBLISHES
    /// a mediator notification. The whole write is provider-agnostic: the module names no store.
    /// </summary>
    public sealed class CreatePostCommandHandler(
        IBlogPostRepository repository,
        IUnitOfWork unitOfWork,
        IMediator mediator)
        : ICommandHandler<CreatePostCommand, WireModel>
    {
        public async Task<WireModel> Handle(CreatePostCommand request, CancellationToken ct)
        {
            var post = BlogPost.Publish(request.ModuleId, request.Title, request.Body, request.Author);

            await repository.AddAsync(post, ct);
            await unitOfWork.SaveChangesAsync(ct);

            await mediator.Publish(
                new BlogPostPublishedNotification(post.Id, post.ModuleId, post.Title.Value, post.Author), ct);

            return BlogPostMapper.ToWire(post);
        }
    }
}
