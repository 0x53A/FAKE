﻿namespace Fake.Deploy.Web
open System
open Nancy
open Nancy.Security
open Nancy.Authentication.Forms
open Fake.Deploy.Web.Data

type Bootstrapper() =
    inherit DefaultNancyBootstrapper()
        
    override this.ConfigureApplicationContainer(container) =
        let m = UserMapper()
        container.Register<IUserMapper, UserMapper>(m) |> ignore
        container.Register<UserMapper, UserMapper>(m) |> ignore
        let c = new Configuration()
        Data.start c
        container.Register<Configuration>(c) |> ignore
        
        let fd (x: TinyIoc.TinyIoCContainer) (y : TinyIoc.NamedParameterOverloads) = c.Data
        container.Register<IDataProvider>(fd) |> ignore
        
        let fm (x: TinyIoc.TinyIoCContainer) (y : TinyIoc.NamedParameterOverloads) = c.Membership
        container.Register<IMembershipProvider>(fm) |> ignore


    override this.ApplicationStartup (container, pipelines) =
        //StaticConfiguration.Caching.EnableRuntimeViewUpdates <- true
        StaticConfiguration.EnableRequestTracing <- true
        StaticConfiguration.DisableErrorTraces <- false
        Csrf.Enable pipelines
        Nancy.Json.JsonSettings.MaxJsonLength <- 1024 * 1024

        base.ApplicationStartup(container, pipelines);
        
    override this.RequestStartup(container, pipelines, context) =
        let c = container.Resolve<Configuration>()

        let formsAuthConfig = FormsAuthenticationConfiguration()
        formsAuthConfig.RedirectUrl <- if c.IsConfigured then "~/Account/Login" else "~/Setup"
        let u = container.Resolve<IUserMapper>()
        formsAuthConfig.UserMapper <- u
        FormsAuthentication.Enable(pipelines, formsAuthConfig)

