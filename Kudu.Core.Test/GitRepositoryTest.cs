
            var repository = new GitExeRepository(Mock.Of<IEnvironment>(), settings.Object, trace.Object);

            var environment = new Mock<IEnvironment>();
            environment.SetupGet(e => e.RepositoryPath)
                       .Returns(String.Empty);
            environment.SetupGet(e => e.SiteRootPath)
                       .Returns(String.Empty);

            var server = new GitExeServer(environment.Object, initLock, null, factory.Object, env.Object, settings.Object, trace.Object);