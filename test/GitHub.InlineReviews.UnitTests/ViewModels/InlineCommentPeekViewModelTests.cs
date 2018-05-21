﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using GitHub.Api;
using GitHub.Factories;
using GitHub.InlineReviews.Services;
using GitHub.InlineReviews.ViewModels;
using GitHub.Models;
using GitHub.Primitives;
using GitHub.Services;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using NSubstitute;
using Octokit;
using NUnit.Framework;
using GitHub.Commands;

namespace GitHub.InlineReviews.UnitTests.ViewModels
{
    public class InlineCommentPeekViewModelTests
    {
        const string FullPath = "c:\\repo\\test.cs";
        const string RelativePath = "test.cs";

        [Test]
        public async Task ThreadIsCreatedForExistingComments()
        {
            // There is an existing comment thread at line 10.
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 10),
                CreatePeekSession(),
                CreateSessionManager(),
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();

            // There should be an existing comment and a reply placeholder.
            Assert.That(target.Thread, Is.InstanceOf(typeof(InlineCommentThreadViewModel)));
            Assert.That(2, Is.EqualTo(target.Thread.Comments.Count));
            Assert.That("Existing comment", Is.EqualTo(target.Thread.Comments[0].Body));
            Assert.That(string.Empty, Is.EqualTo(target.Thread.Comments[1].Body));
            Assert.That(CommentEditState.Placeholder, Is.EqualTo(target.Thread.Comments[1].EditState));
        }

        [Test]
        public async Task ThreadIsCreatedForNewComment()
        {
            // There is no existing comment thread at line 9, but there is a + diff entry.
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 9),
                CreatePeekSession(),
                CreateSessionManager(),
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();

            Assert.That(target.Thread, Is.InstanceOf(typeof(NewInlineCommentThreadViewModel)));
            Assert.That(string.Empty, Is.EqualTo(target.Thread.Comments[0].Body));
            Assert.That(CommentEditState.Creating, Is.EqualTo(target.Thread.Comments[0].EditState));
        }

        [Test]
        public async Task ShouldGetRelativePathFromTextBufferInfoIfPresent()
        {
            var session = CreateSession();
            var bufferInfo = new PullRequestTextBufferInfo(session, RelativePath, "123", DiffSide.Right);
            var sessionManager = CreateSessionManager(
                relativePath: "ShouldNotUseThis",
                session: session,
                textBufferInfo: bufferInfo);

            // There is an existing comment thread at line 10.
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 10),
                CreatePeekSession(),
                sessionManager,
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();

            // There should be an existing comment and a reply placeholder.
            Assert.That(target.Thread, Is.InstanceOf(typeof(InlineCommentThreadViewModel)));
            Assert.That(2, Is.EqualTo(target.Thread.Comments.Count));
            Assert.That("Existing comment", Is.EqualTo(target.Thread.Comments[0].Body));
            Assert.That(string.Empty, Is.EqualTo(target.Thread.Comments[1].Body));
            Assert.That(CommentEditState.Placeholder, Is.EqualTo(target.Thread.Comments[1].EditState));
        }

        [Test]
        public async Task SwitchesFromNewThreadToExistingThreadWhenCommentPosted()
        {
            var sessionManager = CreateSessionManager();
            var peekSession = CreatePeekSession();
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 8),
                peekSession,
                sessionManager,
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();
            Assert.That(target.Thread, Is.InstanceOf(typeof(NewInlineCommentThreadViewModel)));

            target.Thread.Comments[0].Body = "New Comment";

            sessionManager.CurrentSession
                .When(x => x.PostReviewComment(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<DiffChunk>>(),
                    Arg.Any<int>()))
                .Do(async x =>
                {
                    // Simulate the thread being added to the session.
                    var file = await sessionManager.GetLiveFile(
                        RelativePath,
                        peekSession.TextView,
                        peekSession.TextView.TextBuffer);
                    var newThread = CreateThread(8, "New Comment");
                    file.InlineCommentThreads.Returns(new[] { newThread });
                    RaiseLinesChanged(file, Tuple.Create(8, DiffSide.Right));
                });

            await target.Thread.Comments[0].CommitCreate.ExecuteAsyncTask(null);

            Assert.That(target.Thread, Is.InstanceOf(typeof(InlineCommentThreadViewModel)));
        }

        [Test]
        public async Task RefreshesWhenSessionInlineCommentThreadsChanges()
        {
            var sessionManager = CreateSessionManager();
            var peekSession = CreatePeekSession();
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 10),
                peekSession,
                sessionManager,
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();

            Assert.That(target.Thread, Is.InstanceOf(typeof(InlineCommentThreadViewModel)));
            Assert.That(2, Is.EqualTo(target.Thread.Comments.Count));

            var file = await sessionManager.GetLiveFile(
                RelativePath,
                peekSession.TextView,
                peekSession.TextView.TextBuffer);
            AddCommentToExistingThread(file);

            Assert.That(3, Is.EqualTo(target.Thread.Comments.Count));
        }

        [Test]
        public async Task RetainsCommentBeingEditedWhenSessionRefreshed()
        {
            var sessionManager = CreateSessionManager();
            var peekSession = CreatePeekSession();
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 10),
                CreatePeekSession(),
                sessionManager,
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();

            Assert.That(2, Is.EqualTo(target.Thread.Comments.Count));

            var placeholder = target.Thread.Comments.Last();
            placeholder.BeginCreate.Execute(null);
            placeholder.Body = "Comment being edited";

            var file = await sessionManager.GetLiveFile(
                RelativePath,
                peekSession.TextView,
                peekSession.TextView.TextBuffer);
            AddCommentToExistingThread(file);

            placeholder = target.Thread.Comments.Last();
            Assert.That(3, Is.EqualTo(target.Thread.Comments.Count));
            Assert.That(CommentEditState.Creating, Is.EqualTo(placeholder.EditState));
            Assert.That("Comment being edited", Is.EqualTo(placeholder.Body));
        }

        [Test]
        public async Task CommittingEditDoesntRetainSubmittedCommentInPlaceholderAfterPost()
        {
            var sessionManager = CreateSessionManager();
            var peekSession = CreatePeekSession();
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 10),
                peekSession,
                sessionManager,
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();

            Assert.That(2, Is.EqualTo(target.Thread.Comments.Count));

            sessionManager.CurrentSession.PostReviewComment(null, 0, null)
                .ReturnsForAnyArgs(async x =>
                {
                    var file = await sessionManager.GetLiveFile(
                        RelativePath,
                        peekSession.TextView,
                        peekSession.TextView.TextBuffer);
                    AddCommentToExistingThread(file);
                    return file.InlineCommentThreads[0].Comments.Last();
                });

            var placeholder = target.Thread.Comments.Last();
            placeholder.BeginCreate.Execute(null);
            placeholder.Body = "Comment being edited";
            placeholder.CommitCreate.Execute(null);

            placeholder = target.Thread.Comments.Last();
            Assert.That(CommentEditState.Placeholder, Is.EqualTo(placeholder.EditState));
            Assert.That(string.Empty, Is.EqualTo(placeholder.Body));
        }

        [Test]
        public async Task StartingReviewDoesntRetainSubmittedCommentInPlaceholderAfterPost()
        {
            var sessionManager = CreateSessionManager();
            var peekSession = CreatePeekSession();
            var target = new InlineCommentPeekViewModel(
                CreatePeekService(lineNumber: 10),
                peekSession,
                sessionManager,
                Substitute.For<INextInlineCommentCommand>(),
                Substitute.For<IPreviousInlineCommentCommand>());

            await target.Initialize();

            Assert.That(2, Is.EqualTo(target.Thread.Comments.Count));

            sessionManager.CurrentSession.StartReview()
                .ReturnsForAnyArgs(async x =>
                {
                    var file = await sessionManager.GetLiveFile(
                        RelativePath,
                        peekSession.TextView,
                        peekSession.TextView.TextBuffer);
                    RaiseLinesChanged(file, Tuple.Create(10, DiffSide.Right));
                    return Substitute.For<IPullRequestReviewModel>();
                });

            var placeholder = (IPullRequestReviewCommentViewModel)target.Thread.Comments.Last();
            placeholder.BeginCreate.Execute(null);
            placeholder.Body = "Comment being edited";
            placeholder.StartReview.Execute(null);

            placeholder = (IPullRequestReviewCommentViewModel)target.Thread.Comments.Last();
            Assert.That(CommentEditState.Placeholder, Is.EqualTo(placeholder.EditState));
            Assert.That(string.Empty, Is.EqualTo(placeholder.Body));
        }

        void AddCommentToExistingThread(IPullRequestSessionFile file)
        {
            var newThreads = file.InlineCommentThreads.ToList();
            var thread = file.InlineCommentThreads.Single();
            var newComment = CreateComment("New Comment");
            var newComments = thread.Comments.Concat(new[] { newComment }).ToList();
            thread.Comments.Returns(newComments);
            file.InlineCommentThreads.Returns(newThreads);
            RaiseLinesChanged(file, Tuple.Create(thread.LineNumber, DiffSide.Right));
        }

        IApiClientFactory CreateApiClientFactory()
        {
            var apiClient = Substitute.For<IApiClient>();
            apiClient.CreatePullRequestReviewComment(null, null, 0, null, 0)
                .ReturnsForAnyArgs(_ => Observable.Return(new PullRequestReviewComment()));
            apiClient.CreatePullRequestReviewComment(null, null, 0, null, null, null, 0)
                .ReturnsForAnyArgs(_ => Observable.Return(new PullRequestReviewComment()));

            var result = Substitute.For<IApiClientFactory>();
            result.Create(null).ReturnsForAnyArgs(apiClient);
            return result;
        }

        IPullRequestReviewCommentModel CreateComment(string body)
        {
            var comment = Substitute.For<IPullRequestReviewCommentModel>();
            comment.Body.Returns(body);
            return comment;
        }

        IInlineCommentThreadModel CreateThread(int lineNumber, params string[] comments)
        {
            var result = Substitute.For<IInlineCommentThreadModel>();
            var commentList = comments.Select(x => CreateComment(x)).ToList();
            result.Comments.Returns(commentList);
            result.LineNumber.Returns(lineNumber);
            return result;
        }

        IInlineCommentPeekService CreatePeekService(int lineNumber)
        {
            var result = Substitute.For<IInlineCommentPeekService>();
            result.GetLineNumber(null, null).ReturnsForAnyArgs(Tuple.Create(lineNumber, false));
            return result;
        }

        IPeekSession CreatePeekSession()
        {
            var document = Substitute.For<ITextDocument>();
            document.FilePath.Returns(FullPath);

            var propertyCollection = new PropertyCollection();
            propertyCollection.AddProperty(typeof(ITextDocument), document);

            var result = Substitute.For<IPeekSession>();
            result.TextView.TextBuffer.Properties.Returns(propertyCollection);

            return result;
        }

        IPullRequestSession CreateSession()
        {
            var result = Substitute.For<IPullRequestSession>();
            result.LocalRepository.CloneUrl.Returns(new UriString("https://foo.bar"));
            return result;
        }

        IPullRequestSessionManager CreateSessionManager(
            string commitSha = "COMMIT",
            string relativePath = RelativePath,
            IPullRequestSession session = null,
            PullRequestTextBufferInfo textBufferInfo = null)
        {
            var thread = CreateThread(10, "Existing comment");

            var diff = new DiffChunk
            {
                DiffLine = 10,
                OldLineNumber = 1,
                NewLineNumber = 1,
            };

            for (var i = 0; i < 10; ++i)
            {
                diff.Lines.Add(new DiffLine
                {
                    NewLineNumber = i,
                    DiffLineNumber = i + 10,
                    Type = i < 5 ? DiffChangeType.Delete : DiffChangeType.Add,
                });
            }

            var file = Substitute.For<IPullRequestSessionFile>();
            file.CommitSha.Returns(commitSha);
            file.Diff.Returns(new[] { diff });
            file.InlineCommentThreads.Returns(new[] { thread });
            file.LinesChanged.Returns(new Subject<IReadOnlyList<Tuple<int, DiffSide>>>());

            session = session ?? CreateSession();

            if (textBufferInfo != null)
            {
                session.GetFile(textBufferInfo.RelativePath, textBufferInfo.CommitSha).Returns(file);
            }

            var result = Substitute.For<IPullRequestSessionManager>();
            result.CurrentSession.Returns(session);
            result.GetLiveFile(relativePath, Arg.Any<ITextView>(), Arg.Any<ITextBuffer>()).Returns(file);
            result.GetRelativePath(Arg.Any<ITextBuffer>()).Returns(relativePath);
            result.GetTextBufferInfo(Arg.Any<ITextBuffer>()).Returns(textBufferInfo);

            return result;
        }

        void RaiseLinesChanged(IPullRequestSessionFile file, params Tuple<int, DiffSide>[] lineNumbers)
        {
            var subject = (Subject<IReadOnlyList<Tuple<int, DiffSide>>>)file.LinesChanged;
            subject.OnNext(lineNumbers);
        }

        void RaisePropertyChanged<T>(T o, string propertyName)
            where T : INotifyPropertyChanged
        {
            o.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(new PropertyChangedEventArgs(propertyName));
        }
    }
}
