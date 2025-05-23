using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private MethodDeclarationSyntax? CreateMethod(WaylandProtocol protocol, WaylandProtocolInterface @interface, WaylandProtocolMessage request, int index)
        {
            var newIdArgument = request.Arguments?.FirstOrDefault(static a => a.Type == WaylandArgumentTypes.NewId);
            if (newIdArgument is not null && newIdArgument.Interface is null)
                return null;
            var ctorType = newIdArgument?.Interface;
            var dotNetCtorType = ctorType is null ? "void" : GetWlInterfaceTypeName(ctorType);

            var method = MethodDeclaration(ParseTypeName(dotNetCtorType), Pascalize(request.Name));

            var plist = new SeparatedSyntaxList<ParameterSyntax>();
            var arglist = new SeparatedSyntaxList<ExpressionSyntax>();
            var statements = new SeparatedSyntaxList<StatementSyntax>();
            var callStatements = new SeparatedSyntaxList<StatementSyntax>();
            var fixedDeclarations = new List<VariableDeclarationSyntax>();

            if (request.Since > 0)
                statements = statements.Add(IfStatement(
                    BinaryExpression(SyntaxKind.LessThanExpression,
                        IdentifierName("Version"), MakeLiteralExpression(request.Since)),
                    request.Type == "destructor"
                        ? ReturnStatement()
                        : ThrowStatement(ObjectCreationExpression(ParseTypeName("InvalidOperationException"))
                            .WithArgumentList(
                                ArgumentList(SingletonSeparatedList(Argument(MakeLiteralExpression(
                                    $"Request {request.Name} is only supported since version {request.Since}"))))))));

            if (request.Arguments is not null)
                foreach (var arg in request.Arguments ?? Array.Empty<WaylandProtocolArgument>())
                {
                    TypeSyntax? parameterType = null;
                    var nullCheck = false;
                    var argName = $"@{Pascalize(arg.Name, true)}";

                    switch (arg.Type)
                    {
                        case WaylandArgumentTypes.Int32:
                        case WaylandArgumentTypes.Fixed:
                        case WaylandArgumentTypes.FileDescriptor:
                        case WaylandArgumentTypes.UInt32:
                        {
                            var nativeType = arg.Type switch
                            {
                                WaylandArgumentTypes.Int32 => "int",
                                WaylandArgumentTypes.UInt32 => "uint",
                                WaylandArgumentTypes.Fixed => "WlFixed",
                                _ => "int"
                            };

                            var managedType =
                                TryGetEnumTypeReference(protocol.Name, @interface.Name, request.Name, arg.Name, arg.Enum) ??
                                nativeType;

                            parameterType = ParseTypeName(managedType);
                            arglist = nativeType != managedType ? arglist.Add(CastExpression(ParseTypeName(nativeType), IdentifierName(argName))) : arglist.Add(IdentifierName(argName));
                            break;
                        }
                        case WaylandArgumentTypes.NewId:
                            arglist = arglist.Add(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                IdentifierName("WlArgument"), IdentifierName("NewId")));
                            break;
                        case WaylandArgumentTypes.String:
                        {
                            nullCheck = !arg.AllowNull;
                            parameterType = arg.AllowNull ? NullableType(ParseTypeName("string")) : ParseTypeName("string");
                            var tempName = "__marshalled__" + argName.TrimStart('@');
                            var bufferType = ParseTypeName("NWaylandMarshalledString");

                            statements = statements.Add(LocalDeclarationStatement(
                                new SyntaxTokenList(Token(SyntaxKind.UsingKeyword)),
                                VariableDeclaration(ParseTypeName("var"))
                                    .WithVariables(SingletonSeparatedList(
                                        VariableDeclarator(tempName)
                                            .WithInitializer(EqualsValueClause(ObjectCreationExpression(bufferType)
                                                .WithArgumentList(
                                                    ArgumentList(
                                                        SingletonSeparatedList(Argument(IdentifierName(argName)))))))
                                    ))));
                            arglist = arglist.Add(IdentifierName(tempName));
                            break;
                        }
                        case WaylandArgumentTypes.Object:
                            nullCheck = !arg.AllowNull;
                            parameterType = arg.AllowNull ? NullableType(ParseTypeName(GetWlInterfaceTypeName(arg.Interface))) : ParseTypeName(GetWlInterfaceTypeName(arg.Interface));
                            arglist = arglist.Add(IdentifierName(argName));
                            break;
                        case WaylandArgumentTypes.Array when arg.AllowNull:
                            throw new NotSupportedException("Wrapping nullable arrays is currently not supported");
                        case WaylandArgumentTypes.Array:
                        {
                            var arrayElementType = GetTypeNameForArray(protocol.Name, @interface.Name, request.Name, arg.Name);
                            parameterType = ParseTypeName($"ReadOnlySpan<{arrayElementType}>");
                            var pointerName = "__pointer__" + argName.TrimStart('@');
                            var tempName = "__marshalled__" + argName.TrimStart('@');
                            fixedDeclarations.Add(VariableDeclaration(ParseTypeName(arrayElementType + '*'),
                                SingletonSeparatedList(VariableDeclarator(pointerName)
                                    .WithInitializer(EqualsValueClause(IdentifierName(argName))))));

                            callStatements = callStatements.Add(LocalDeclarationStatement(VariableDeclaration(ParseTypeName("var"))
                                .WithVariables(SingletonSeparatedList(VariableDeclarator(tempName)
                                    .WithInitializer(EqualsValueClause(
                                        InvocationExpression(MemberAccess(ParseTypeName("WlArray"),
                                            "FromPointer"), ArgumentList(SeparatedList(new[]
                                            {
                                                Argument(IdentifierName(pointerName)),
                                                Argument(MemberAccess(IdentifierName(argName), "Length"))
                                            }
                                        ))))

                                    )))));

                            arglist = arglist.Add(PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                IdentifierName(tempName)));
                            break;
                        }
                    }

                    if (parameterType is not null)
                        plist = plist.Add(Parameter(Identifier(argName)).WithType(parameterType));

                    if (nullCheck)
                        statements = statements.Insert(0, IfStatement(
                            BinaryExpression(SyntaxKind.EqualsExpression, IdentifierName(argName),
                                MakeNullLiteralExpression()),
                            ThrowStatement(ObjectCreationExpression(ParseTypeName("ArgumentNullException"))
                                .WithArgumentList(
                                    ArgumentList(
                                        SingletonSeparatedList(
                                            Argument(MakeLiteralExpression(argName.TrimStart('@')))))))));
                }

            callStatements = callStatements.Add(LocalDeclarationStatement(VariableDeclaration(ParseTypeName("WlArgument*"))
                .WithVariables(SingletonSeparatedList(VariableDeclarator("__args")
                    .WithInitializer(EqualsValueClause(StackAllocArrayCreationExpression(
                        ArrayType(ParseTypeName("WlArgument[]")),
                        InitializerExpression(SyntaxKind.ArrayInitializerExpression, arglist))))))));

            var marshalArgs = SeparatedList(new[]
            {
                Argument(MemberAccess(ThisExpression(), "Handle")),
                Argument(MakeLiteralExpression(index)),
                Argument(IdentifierName("__args"))
            });

            if (ctorType is not null)
                marshalArgs = marshalArgs.Add(Argument(GetWlInterfaceRefFor(ctorType)))
                    .Add(Argument(CastExpression(ParseTypeName("uint"), MemberAccess(ThisExpression(), "Version"))));

            var callExpr = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("LibWayland"),
                    IdentifierName(ctorType is null ? "wl_proxy_marshal_array" : "wl_proxy_marshal_array_constructor_versioned")),
                ArgumentList(marshalArgs));

            if (ctorType is null)
            {
                callStatements = callStatements.Add(ExpressionStatement(callExpr));
            }
            else
            {
                callStatements = callStatements.Add(LocalDeclarationStatement(VariableDeclaration(ParseTypeName("var"))
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator("__ret").WithInitializer(EqualsValueClause(callExpr))))));
                callStatements = callStatements.Add(ReturnStatement(ConditionalExpression(BinaryExpression(
                        SyntaxKind.EqualsExpression, IdentifierName("__ret"),
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("IntPtr"),
                            IdentifierName("Zero"))),
                    MakeNullLiteralExpression(),
                    ObjectCreationExpression(ParseTypeName(dotNetCtorType)).WithArgumentList(
                        ArgumentList(SeparatedList(new[]
                        {
                            Argument(IdentifierName("__ret")),
                            Argument(IdentifierName("Version"))
                        }))))));
            }

            if (fixedDeclarations.Count == 0)
            {
                statements = statements.AddRange(callStatements);
            }
            else
            {
                var callBlock = (StatementSyntax)Block(callStatements);
                fixedDeclarations.Reverse();
                foreach (var fd in fixedDeclarations)
                    callBlock = FixedStatement(fd, callBlock);
                statements = statements.Add(callBlock);
            }

            method = WithSummary(method.WithParameterList(ParameterList(plist))
                    .WithBody(Block(statements))
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword))),
                request.Description);

            if (request.Type == "destructor")
                method = method
                    .WithIdentifier(Identifier("Dispose"))
                    .WithParameterList(ParameterList(SeparatedList(new []{ Parameter(Identifier("disposing")).WithType(ParseTypeName("bool")) })))
                    .WithModifiers(TokenList(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword)))
                    .AddBodyStatements(ExpressionStatement(
                        InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, BaseExpression(), IdentifierName("Dispose")),
                            ArgumentList(SeparatedList(new []{ Argument(LiteralExpression(SyntaxKind.TrueLiteralExpression)) })))
                    ));

            return method;
        }


        private ClassDeclarationSyntax WithRequests(ClassDeclarationSyntax cl, WaylandProtocol protocol, WaylandProtocolInterface @interface)
        {
            if (@interface.Requests is null)
                return cl;
            for (var idx = 0; idx < @interface.Requests.Length; idx++)
            {
                var method = CreateMethod(protocol, @interface, @interface.Requests[idx], idx);
                if (method is not null)
                    cl = cl.AddMembers(method);
            }

            return cl;
        }
    }
}
