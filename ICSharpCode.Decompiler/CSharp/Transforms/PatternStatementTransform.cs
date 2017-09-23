﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.Decompiler.CSharp.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;
using ICSharpCode.Decompiler.Semantics;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// Finds the expanded form of using statements using pattern matching and replaces it with a UsingStatement.
	/// </summary>
	public sealed class PatternStatementTransform : ContextTrackingVisitor<AstNode>, IAstTransform
	{
		readonly DeclareVariables declareVariables = new DeclareVariables();
		TransformContext context;
		
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (this.context != null)
				throw new InvalidOperationException("Reentrancy in PatternStatementTransform.Run?");
			try {
				this.context = context;
				base.Initialize(context);
				declareVariables.Analyze(rootNode);
				rootNode.AcceptVisitor(this);
			} finally {
				this.context = null;
				base.Uninitialize();
				declareVariables.ClearAnalysisResults();
			}
		}

		#region Visitor Overrides
		protected override AstNode VisitChildren(AstNode node)
		{
			// Go through the children, and keep visiting a node as long as it changes.
			// Because some transforms delete/replace nodes before and after the node being transformed, we rely
			// on the transform's return value to know where we need to keep iterating.
			for (AstNode child = node.FirstChild; child != null; child = child.NextSibling) {
				AstNode oldChild;
				do {
					oldChild = child;
					child = child.AcceptVisitor(this);
					Debug.Assert(child != null && child.Parent == node);
				} while (child != oldChild);
			}
			return node;
		}
		
		public override AstNode VisitExpressionStatement(ExpressionStatement expressionStatement)
		{
			AstNode result;
			result = TransformFor(expressionStatement);
			if (result != null)
				return result;
			if (context.Settings.AutomaticProperties) {
				result = ReplaceBackingFieldUsage(expressionStatement);
				if (result != null)
					return result;
			}
			if (context.Settings.AutomaticEvents) {
				result = ReplaceEventFieldAnnotation(expressionStatement);
				if (result != null)
					return result;
			}
			return base.VisitExpressionStatement(expressionStatement);
		}
		
		public override AstNode VisitWhileStatement(WhileStatement whileStatement)
		{
			return TransformDoWhile(whileStatement) ?? base.VisitWhileStatement(whileStatement);
		}
		
		public override AstNode VisitIfElseStatement(IfElseStatement ifElseStatement)
		{
			if (context.Settings.SwitchStatementOnString) {
				AstNode result = TransformSwitchOnString(ifElseStatement);
				if (result != null)
					return result;
			}
			AstNode simplifiedIfElse = SimplifyCascadingIfElseStatements(ifElseStatement);
			if (simplifiedIfElse != null)
				return simplifiedIfElse;
			return base.VisitIfElseStatement(ifElseStatement);
		}
		
		public override AstNode VisitPropertyDeclaration(PropertyDeclaration propertyDeclaration)
		{
			if (context.Settings.AutomaticProperties) {
				AstNode result = TransformAutomaticProperties(propertyDeclaration);
				if (result != null)
					return result;
			}
			return base.VisitPropertyDeclaration(propertyDeclaration);
		}
		
		public override AstNode VisitCustomEventDeclaration(CustomEventDeclaration eventDeclaration)
		{
			// first apply transforms to the accessor bodies
			base.VisitCustomEventDeclaration(eventDeclaration);
			if (context.Settings.AutomaticEvents) {
				AstNode result = TransformAutomaticEvents(eventDeclaration);
				if (result != null)
					return result;
			}
			return eventDeclaration;
		}
		
		public override AstNode VisitMethodDeclaration(MethodDeclaration methodDeclaration)
		{
			return TransformDestructor(methodDeclaration) ?? base.VisitMethodDeclaration(methodDeclaration);
		}
		
		public override AstNode VisitTryCatchStatement(TryCatchStatement tryCatchStatement)
		{
			return TransformTryCatchFinally(tryCatchStatement) ?? base.VisitTryCatchStatement(tryCatchStatement);
		}
		#endregion
		
		/// <summary>
		/// $variable = $initializer;
		/// </summary>
		static readonly AstNode variableAssignPattern = new ExpressionStatement(
			new AssignmentExpression(
				new NamedNode("variable", new IdentifierExpression(Pattern.AnyString)),
				new AnyNode("initializer")
			));

		#region for
		static readonly WhileStatement forPattern = new WhileStatement {
			Condition = new BinaryOperatorExpression {
				Left = new NamedNode("ident", new IdentifierExpression(Pattern.AnyString)),
				Operator = BinaryOperatorType.Any,
				Right = new AnyNode("endExpr")
			},
			EmbeddedStatement = new BlockStatement {
				Statements = {
					new Repeat(new AnyNode("statement")),
					new NamedNode(
						"increment",
						new ExpressionStatement(
							new AssignmentExpression {
								Left = new Backreference("ident"),
								Operator = AssignmentOperatorType.Any,
								Right = new AnyNode()
							}))
				}
			}};

		public ForStatement TransformFor(ExpressionStatement node)
		{
			Match m1 = variableAssignPattern.Match(node);
			if (!m1.Success) return null;
			var variableName = m1.Get<IdentifierExpression>("variable").Single().Identifier;
			AstNode next = node.NextSibling;
			if (next is ForStatement forStatement) {
				if ((forStatement.Iterators.FirstOrDefault() is ExpressionStatement stmt
					&& stmt.Expression is AssignmentExpression assign
					&& variableName == assign.Left.ToString())
				|| (forStatement.Condition is BinaryOperatorExpression cond
					&& variableName == cond.Left.ToString()))
				{
					node.Remove();
					forStatement.InsertChildAfter(null, node, ForStatement.InitializerRole);
					return forStatement;
				}
			}
			Match m3 = forPattern.Match(next);
			if (!m3.Success) return null;
			// ensure the variable in the for pattern is the same as in the declaration
			if (variableName != m3.Get<IdentifierExpression>("ident").Single().Identifier)
				return null;
			WhileStatement loop = (WhileStatement)next;
			node.Remove();
			BlockStatement newBody = new BlockStatement();
			foreach (Statement stmt in m3.Get<Statement>("statement"))
				newBody.Add(stmt.Detach());
			forStatement = new ForStatement();
			forStatement.CopyAnnotationsFrom(loop);
			forStatement.Initializers.Add(node);
			forStatement.Condition = loop.Condition.Detach();
			forStatement.Iterators.Add(m3.Get<Statement>("increment").Single().Detach());
			forStatement.EmbeddedStatement = newBody;
			loop.ReplaceWith(forStatement);
			return forStatement;
		}
		#endregion
		
		#region doWhile
		static readonly WhileStatement doWhilePattern = new WhileStatement {
			Condition = new PrimitiveExpression(true),
			EmbeddedStatement = new BlockStatement {
				Statements = {
					new Repeat(new AnyNode("statement")),
					new IfElseStatement {
						Condition = new AnyNode("condition"),
						TrueStatement = new BlockStatement { new BreakStatement() }
					}
				}
			}};
		
		public DoWhileStatement TransformDoWhile(WhileStatement whileLoop)
		{
			Match m = doWhilePattern.Match(whileLoop);
			if (m.Success) {
				DoWhileStatement doLoop = new DoWhileStatement();
				doLoop.Condition = new UnaryOperatorExpression(UnaryOperatorType.Not, m.Get<Expression>("condition").Single().Detach());
				//doLoop.Condition.AcceptVisitor(new PushNegation(), null);
				BlockStatement block = (BlockStatement)whileLoop.EmbeddedStatement;
				block.Statements.Last().Remove(); // remove if statement
				doLoop.EmbeddedStatement = block.Detach();
				doLoop.CopyAnnotationsFrom(whileLoop);
				whileLoop.ReplaceWith(doLoop);
				
				// we may have to extract variable definitions out of the loop if they were used in the condition:
				foreach (var varDecl in block.Statements.OfType<VariableDeclarationStatement>()) {
					VariableInitializer v = varDecl.Variables.Single();
					if (doLoop.Condition.DescendantsAndSelf.OfType<IdentifierExpression>().Any(i => i.Identifier == v.Name)) {
						AssignmentExpression assign = new AssignmentExpression(new IdentifierExpression(v.Name), v.Initializer.Detach());
						// move annotations from v to assign:
						assign.CopyAnnotationsFrom(v);
						v.RemoveAnnotations<object>();
						// remove varDecl with assignment; and move annotations from varDecl to the ExpressionStatement:
						varDecl.ReplaceWith(new ExpressionStatement(assign).CopyAnnotationsFrom(varDecl));
						varDecl.RemoveAnnotations<object>();
						
						// insert the varDecl above the do-while loop:
						doLoop.Parent.InsertChildBefore(doLoop, varDecl, BlockStatement.StatementRole);
					}
				}
				return doLoop;
			}
			return null;
		}
		#endregion
		
		#region switch on strings
		static readonly IfElseStatement switchOnStringPattern = new IfElseStatement {
			Condition = new BinaryOperatorExpression {
				Left = new AnyNode("switchExpr"),
				Operator = BinaryOperatorType.InEquality,
				Right = new NullReferenceExpression()
			},
			TrueStatement = new BlockStatement {
				new IfElseStatement {
					Condition = new BinaryOperatorExpression {
						Left = new AnyNode("cachedDict"),
						Operator = BinaryOperatorType.Equality,
						Right = new NullReferenceExpression()
					},
					TrueStatement = new AnyNode("dictCreation")
				},
				new IfElseStatement {
					Condition = new InvocationExpression(new MemberReferenceExpression(new Backreference("cachedDict").ToExpression(), "TryGetValue"),
						new NamedNode("switchVar", new IdentifierExpression(Pattern.AnyString)),
						new DirectionExpression {
							FieldDirection = FieldDirection.Out,
							Expression = new IdentifierExpression(Pattern.AnyString).WithName("intVar")
						}),
					TrueStatement = new BlockStatement {
						Statements = {
							new NamedNode(
								"switch", new SwitchStatement {
									Expression = new IdentifierExpressionBackreference("intVar"),
									SwitchSections = { new Repeat(new AnyNode()) }
								})
						}
					}
				},
				new Repeat(new AnyNode("nonNullDefaultStmt")).ToStatement()
			},
			FalseStatement = new OptionalNode("nullStmt", new BlockStatement { Statements = { new Repeat(new AnyNode()) } })
		};
		
		public SwitchStatement TransformSwitchOnString(IfElseStatement node)
		{
			Match m = switchOnStringPattern.Match(node);
			if (!m.Success)
				return null;
			// switchVar must be the same as switchExpr; or switchExpr must be an assignment and switchVar the left side of that assignment
			if (!m.Get("switchVar").Single().IsMatch(m.Get("switchExpr").Single())) {
				AssignmentExpression assign = m.Get("switchExpr").Single() as AssignmentExpression;
				if (!(assign != null && m.Get("switchVar").Single().IsMatch(assign.Left)))
					return null;
			}
			FieldReference cachedDictField = m.Get<AstNode>("cachedDict").Single().Annotation<FieldReference>();
			if (cachedDictField == null)
				return null;
			List<Statement> dictCreation = m.Get<BlockStatement>("dictCreation").Single().Statements.ToList();
			List<KeyValuePair<string, int>> dict = BuildDictionary(dictCreation);
			SwitchStatement sw = m.Get<SwitchStatement>("switch").Single();
			sw.Expression = m.Get<Expression>("switchExpr").Single().Detach();
			foreach (SwitchSection section in sw.SwitchSections) {
				List<CaseLabel> labels = section.CaseLabels.ToList();
				section.CaseLabels.Clear();
				foreach (CaseLabel label in labels) {
					PrimitiveExpression expr = label.Expression as PrimitiveExpression;
					if (expr == null || !(expr.Value is int))
						continue;
					int val = (int)expr.Value;
					foreach (var pair in dict) {
						if (pair.Value == val)
							section.CaseLabels.Add(new CaseLabel { Expression = new PrimitiveExpression(pair.Key) });
					}
				}
			}
			if (m.Has("nullStmt")) {
				SwitchSection section = new SwitchSection();
				section.CaseLabels.Add(new CaseLabel { Expression = new NullReferenceExpression() });
				BlockStatement block = m.Get<BlockStatement>("nullStmt").Single();
				block.Statements.Add(new BreakStatement());
				section.Statements.Add(block.Detach());
				sw.SwitchSections.Add(section);
			} else if (m.Has("nonNullDefaultStmt")) {
				sw.SwitchSections.Add(
					new SwitchSection {
						CaseLabels = { new CaseLabel { Expression = new NullReferenceExpression() } },
						Statements = { new BlockStatement { new BreakStatement() } }
					});
			}
			if (m.Has("nonNullDefaultStmt")) {
				SwitchSection section = new SwitchSection();
				section.CaseLabels.Add(new CaseLabel());
				BlockStatement block = new BlockStatement();
				block.Statements.AddRange(m.Get<Statement>("nonNullDefaultStmt").Select(s => s.Detach()));
				block.Add(new BreakStatement());
				section.Statements.Add(block);
				sw.SwitchSections.Add(section);
			}
			node.ReplaceWith(sw);
			return sw;
		}
		
		List<KeyValuePair<string, int>> BuildDictionary(List<Statement> dictCreation)
		{
			if (context.Settings.ObjectOrCollectionInitializers && dictCreation.Count == 1)
				return BuildDictionaryFromInitializer(dictCreation[0]);

			return BuildDictionaryFromAddMethodCalls(dictCreation);
		}

		static readonly Statement assignInitializedDictionary = new ExpressionStatement {
			Expression = new AssignmentExpression {
				Left = new AnyNode().ToExpression(),
				Right = new ObjectCreateExpression {
					Type = new AnyNode(),
					Arguments = { new Repeat(new AnyNode()) },
					Initializer = new ArrayInitializerExpression {
						Elements = { new Repeat(new AnyNode("dictJumpTable")) }
					}
				},
			},
		};

		private List<KeyValuePair<string, int>> BuildDictionaryFromInitializer(Statement statement)
		{
			List<KeyValuePair<string, int>> dict = new List<KeyValuePair<string, int>>();
			Match m = assignInitializedDictionary.Match(statement);
			if (!m.Success)
				return dict;

			foreach (ArrayInitializerExpression initializer in m.Get<ArrayInitializerExpression>("dictJumpTable")) {
				KeyValuePair<string, int> pair;
				if (TryGetPairFrom(initializer.Elements, out pair))
					dict.Add(pair);
			}

			return dict;
		}

		private static List<KeyValuePair<string, int>> BuildDictionaryFromAddMethodCalls(List<Statement> dictCreation)
		{
			List<KeyValuePair<string, int>> dict = new List<KeyValuePair<string, int>>();
			for (int i = 0; i < dictCreation.Count; i++) {
				ExpressionStatement es = dictCreation[i] as ExpressionStatement;
				if (es == null)
					continue;
				InvocationExpression ie = es.Expression as InvocationExpression;
				if (ie == null)
					continue;

				KeyValuePair<string, int> pair;
				if (TryGetPairFrom(ie.Arguments, out pair))
					dict.Add(pair);
			}
			return dict;
		}

		private static bool TryGetPairFrom(AstNodeCollection<Expression> expressions, out KeyValuePair<string, int> pair)
		{
			PrimitiveExpression arg1 = expressions.ElementAtOrDefault(0) as PrimitiveExpression;
			PrimitiveExpression arg2 = expressions.ElementAtOrDefault(1) as PrimitiveExpression;
			if (arg1 != null && arg2 != null && arg1.Value is string && arg2.Value is int) {
				pair = new KeyValuePair<string, int>((string)arg1.Value, (int)arg2.Value);
				return true;
			}

			pair = default(KeyValuePair<string, int>);
			return false;
		}

		#endregion
		
		#region Automatic Properties
		static readonly PropertyDeclaration automaticPropertyPattern = new PropertyDeclaration {
			Attributes = { new Repeat(new AnyNode()) },
			Modifiers = Modifiers.Any,
			ReturnType = new AnyNode(),
			PrivateImplementationType = new OptionalNode(new AnyNode()),
			Name = Pattern.AnyString,
			Getter = new Accessor {
				Attributes = { new Repeat(new AnyNode()) },
				Modifiers = Modifiers.Any,
				Body = new BlockStatement {
					new ReturnStatement {
						Expression = new AnyNode("fieldReference")
					}
				}
			},
			Setter = new Accessor {
				Attributes = { new Repeat(new AnyNode()) },
				Modifiers = Modifiers.Any,
				Body = new BlockStatement {
					new AssignmentExpression {
						Left = new Backreference("fieldReference"),
						Right = new IdentifierExpression("value")
					}
				}}
		};

		static readonly PropertyDeclaration automaticReadonlyPropertyPattern = new PropertyDeclaration {
			Attributes = { new Repeat(new AnyNode()) },
			Modifiers = Modifiers.Any,
			ReturnType = new AnyNode(),
			PrivateImplementationType = new OptionalNode(new AnyNode()),
			Name = Pattern.AnyString,
			Getter = new Accessor {
				Attributes = { new Repeat(new AnyNode()) },
				Modifiers = Modifiers.Any,
				Body = new BlockStatement {
					new ReturnStatement {
						Expression = new AnyNode("fieldReference")
					}
				}
			}
		};

		PropertyDeclaration TransformAutomaticProperties(PropertyDeclaration property)
		{
			PropertyDefinition cecilProperty = context.TypeSystem.GetCecil(property.GetSymbol() as IProperty) as PropertyDefinition;
			if (cecilProperty == null || cecilProperty.GetMethod == null)
				return null;
			if (!cecilProperty.GetMethod.IsCompilerGenerated() && (cecilProperty.SetMethod?.IsCompilerGenerated() == false))
				return null;
			IField fieldInfo = null;
			Match m = automaticPropertyPattern.Match(property);
			if (m.Success) {
				fieldInfo = m.Get<AstNode>("fieldReference").Single().GetSymbol() as IField;
			} else {
				Match m2 = automaticReadonlyPropertyPattern.Match(property);
				if (m2.Success) {
					fieldInfo = m2.Get<AstNode>("fieldReference").Single().GetSymbol() as IField;
				}
			}
			if (fieldInfo == null)
				return null;
			FieldDefinition field = context.TypeSystem.GetCecil(fieldInfo) as FieldDefinition;
			if (field.IsCompilerGenerated() && field.DeclaringType == cecilProperty.DeclaringType) {
				RemoveCompilerGeneratedAttribute(property.Getter.Attributes);
				RemoveCompilerGeneratedAttribute(property.Setter.Attributes);
				property.Getter.Body = null;
				property.Setter.Body = null;
			}
			// Since the property instance is not changed, we can continue in the visitor as usual, so return null
			return null;
		}
		
		void RemoveCompilerGeneratedAttribute(AstNodeCollection<AttributeSection> attributeSections)
		{
			foreach (AttributeSection section in attributeSections) {
				foreach (var attr in section.Attributes) {
					var tr = attr.Type.GetSymbol() as IType;
					if (tr != null && tr.Namespace == "System.Runtime.CompilerServices" && tr.Name == "CompilerGeneratedAttribute") {
						attr.Remove();
					}
				}
				if (section.Attributes.Count == 0)
					section.Remove();
			}
		}

		ExpressionStatement ReplaceBackingFieldUsage(ExpressionStatement expressionStatement)
		{
			foreach (var identifier in expressionStatement.Descendants.OfType<Identifier>()) {
				if (identifier.Name.StartsWith("<") && identifier.Name.EndsWith(">k__BackingField")) {
					var parent = identifier.Parent;
					var mrr = parent.Annotation<MemberResolveResult>();
					var field = mrr?.Member as IField;
					if (field != null && field.IsCompilerGenerated()) {
						var propertyName = identifier.Name.Substring(1, identifier.Name.Length - 1 - ">k__BackingField".Length);
						var property = field.DeclaringTypeDefinition.GetProperties(p => p.Name == propertyName, GetMemberOptions.IgnoreInheritedMembers).FirstOrDefault();
						if (property != null) {
							identifier.ReplaceWith(Identifier.Create(propertyName));
							parent.RemoveAnnotations<MemberResolveResult>();
							parent.AddAnnotation(new MemberResolveResult(mrr.TargetResult, property));
						}
					}
				}
			}
			return null;
		}

		ExpressionStatement ReplaceEventFieldAnnotation(ExpressionStatement expressionStatement)
		{
			foreach (var identifier in expressionStatement.Descendants.OfType<Identifier>()) {
				var parent = identifier.Parent;
				var mrr = parent.Annotation<MemberResolveResult>();
				var field = mrr?.Member as IField;
				if (field == null) continue;
				var @event = field.DeclaringType.GetEvents(ev => ev.Name == field.Name, GetMemberOptions.IgnoreInheritedMembers).SingleOrDefault();
				if (@event != null) {
					parent.RemoveAnnotations<MemberResolveResult>();
					parent.AddAnnotation(new MemberResolveResult(mrr.TargetResult, @event));
				}
			}
			return null;
		}
		#endregion

		#region Automatic Events
		static readonly Accessor automaticEventPatternV4 = new Accessor {
			Attributes = { new Repeat(new AnyNode()) },
			Body = new BlockStatement {
				new AssignmentExpression {
					Left = new NamedNode("var1", new IdentifierExpression(Pattern.AnyString)),
					Operator = AssignmentOperatorType.Assign,
					Right = new NamedNode(
						"field",
						new MemberReferenceExpression {
							Target = new Choice { new ThisReferenceExpression(), new TypeReferenceExpression { Type = new AnyNode() } },
							MemberName = Pattern.AnyString
						})
				},
				new WhileStatement {
					Condition = new PrimitiveExpression(true),
					EmbeddedStatement = new BlockStatement {
						new AssignmentExpression(new NamedNode("var2", new IdentifierExpression(Pattern.AnyString)), new IdentifierExpressionBackreference("var1")),
						new AssignmentExpression {
							Left = new NamedNode("var3", new IdentifierExpression(Pattern.AnyString)),
							Operator = AssignmentOperatorType.Assign,
							Right = new CastExpression(new AnyNode("type"), new InvocationExpression(new AnyNode("delegateCombine").ToExpression(), new IdentifierExpressionBackreference("var2"), new IdentifierExpression("value")))
						},
						new AssignmentExpression {
							Left = new IdentifierExpressionBackreference("var1"),
							Right = new InvocationExpression(new MemberReferenceExpression(new TypeReferenceExpression(new TypePattern(typeof(System.Threading.Interlocked)).ToType()),
								"CompareExchange",
								new AstType[] { new Backreference("type") }), // type argument
								new Expression[] { // arguments
									new DirectionExpression { FieldDirection = FieldDirection.Ref, Expression = new Backreference("field") },
									new IdentifierExpressionBackreference("var3"),
									new IdentifierExpressionBackreference("var2")
								}
							)},
						new IfElseStatement {
							Condition = new BinaryOperatorExpression {
								Left = new CastExpression(new TypePattern(typeof(object)), new IdentifierExpressionBackreference("var1")),
								Operator = BinaryOperatorType.Equality,
								Right = new IdentifierExpressionBackreference("var2")
							},
							TrueStatement = new BreakStatement()
						}
					}
				}
			}};
		
		bool CheckAutomaticEventV4Match(Match m, CustomEventDeclaration ev, bool isAddAccessor)
		{
			if (!m.Success)
				return false;
			if (m.Get<MemberReferenceExpression>("field").Single().MemberName != ev.Name)
				return false; // field name must match event name
			if (!ev.ReturnType.IsMatch(m.Get("type").Single()))
				return false; // variable types must match event type
			var combineMethod = m.Get<AstNode>("delegateCombine").Single().Parent.GetSymbol() as IMethod;
			if (combineMethod == null || combineMethod.Name != (isAddAccessor ? "Combine" : "Remove"))
				return false;
			return combineMethod.DeclaringType.FullName == "System.Delegate";
		}

		static readonly string[] attributeTypesToRemoveFromAutoEvents = new[] {
			"System.Runtime.CompilerServices.CompilerGeneratedAttribute",
			"System.Diagnostics.DebuggerBrowsableAttribute"
		};
		
		EventDeclaration TransformAutomaticEvents(CustomEventDeclaration ev)
		{
			Match m1 = automaticEventPatternV4.Match(ev.AddAccessor);
			if (!CheckAutomaticEventV4Match(m1, ev, true))
				return null;
			Match m2 = automaticEventPatternV4.Match(ev.RemoveAccessor);
			if (!CheckAutomaticEventV4Match(m2, ev, false))
				return null;
			RemoveCompilerGeneratedAttribute(ev.AddAccessor.Attributes);
			EventDeclaration ed = new EventDeclaration();
			ev.Attributes.MoveTo(ed.Attributes);
			foreach (var attr in ev.AddAccessor.Attributes) {
				attr.AttributeTarget = "method";
				ed.Attributes.Add(attr.Detach());
			}
			ed.ReturnType = ev.ReturnType.Detach();
			ed.Modifiers = ev.Modifiers;
			ed.Variables.Add(new VariableInitializer(ev.Name));
			ed.CopyAnnotationsFrom(ev);
			
			IEvent eventDef = ev.GetSymbol() as IEvent;
			if (eventDef != null) {
				IField field = eventDef.DeclaringType.GetFields(f => f.Name == ev.Name, GetMemberOptions.IgnoreInheritedMembers).SingleOrDefault();
				if (field != null) {
					ed.AddAnnotation(field);
					var attributes = field.Attributes
							.Where(a => !attributeTypesToRemoveFromAutoEvents.Any(t => t == a.AttributeType.FullName))
							.Select(context.TypeSystemAstBuilder.ConvertAttribute).ToArray();
					if (attributes.Length > 0) {
						var section = new AttributeSection {
							AttributeTarget = "field"
						};
						section.Attributes.AddRange(attributes);
						ed.Attributes.Add(section);
					}
				}
			}
			
			ev.ReplaceWith(ed);
			return ed;
		}
		#endregion
		
		#region Destructor
		static readonly MethodDeclaration destructorPattern = new MethodDeclaration {
			Attributes = { new Repeat(new AnyNode()) },
			Modifiers = Modifiers.Any,
			ReturnType = new PrimitiveType("void"),
			Name = "Finalize",
			Body = new BlockStatement {
				new TryCatchStatement {
					TryBlock = new AnyNode("body"),
					FinallyBlock = new BlockStatement {
						new InvocationExpression(new MemberReferenceExpression(new BaseReferenceExpression(), "Finalize"))
					}
				}
			}
		};
		
		DestructorDeclaration TransformDestructor(MethodDeclaration methodDef)
		{
			Match m = destructorPattern.Match(methodDef);
			if (m.Success) {
				DestructorDeclaration dd = new DestructorDeclaration();
				methodDef.Attributes.MoveTo(dd.Attributes);
				dd.CopyAnnotationsFrom(methodDef);
				dd.Modifiers = methodDef.Modifiers & ~(Modifiers.Protected | Modifiers.Override);
				dd.Body = m.Get<BlockStatement>("body").Single().Detach();
				dd.Name = currentTypeDefinition?.Name;
				methodDef.ReplaceWith(dd);
				return dd;
			}
			return null;
		}
		#endregion
		
		#region Try-Catch-Finally
		static readonly TryCatchStatement tryCatchFinallyPattern = new TryCatchStatement {
			TryBlock = new BlockStatement {
				new TryCatchStatement {
					TryBlock = new AnyNode(),
					CatchClauses = { new Repeat(new AnyNode()) }
				}
			},
			FinallyBlock = new AnyNode()
		};
		
		/// <summary>
		/// Simplify nested 'try { try {} catch {} } finally {}'.
		/// This transformation must run after the using/lock tranformations.
		/// </summary>
		TryCatchStatement TransformTryCatchFinally(TryCatchStatement tryFinally)
		{
			if (tryCatchFinallyPattern.IsMatch(tryFinally)) {
				TryCatchStatement tryCatch = (TryCatchStatement)tryFinally.TryBlock.Statements.Single();
				tryFinally.TryBlock = tryCatch.TryBlock.Detach();
				tryCatch.CatchClauses.MoveTo(tryFinally.CatchClauses);
			}
			// Since the tryFinally instance is not changed, we can continue in the visitor as usual, so return null
			return null;
		}
		#endregion

		#region Simplify cascading if-else-if statements
		static readonly IfElseStatement cascadingIfElsePattern = new IfElseStatement
		{
			Condition = new AnyNode(),
			TrueStatement = new AnyNode(),
			FalseStatement = new BlockStatement {
				Statements = {
					new NamedNode(
						"nestedIfStatement",
						new IfElseStatement {
							Condition = new AnyNode(),
							TrueStatement = new AnyNode(),
							FalseStatement = new OptionalNode(new AnyNode())
						}
					)
				}
			}
		};

		AstNode SimplifyCascadingIfElseStatements(IfElseStatement node)
		{
			Match m = cascadingIfElsePattern.Match(node);
			if (m.Success) {
				IfElseStatement elseIf = m.Get<IfElseStatement>("nestedIfStatement").Single();
				node.FalseStatement = elseIf.Detach();
			}
			
			return null;
		}

		/// <summary>
		/// Use associativity of logic operators to avoid parentheses.
		/// </summary>
		public override AstNode VisitBinaryOperatorExpression(BinaryOperatorExpression boe1)
		{
			switch (boe1.Operator) {
				case BinaryOperatorType.ConditionalAnd:
				case BinaryOperatorType.ConditionalOr:
					// a && (b && c) ==> (a && b) && c
					var boe2 = boe1.Right as BinaryOperatorExpression;
					if (boe2 != null && boe2.Operator == boe1.Operator) {
						// make boe2 the parent and boe1 the child
						var b = boe2.Left.Detach();
						boe1.ReplaceWith(boe2.Detach());
						boe2.Left = boe1;
						boe1.Right = b;
						return base.VisitBinaryOperatorExpression(boe2);
					}
					break;
			}
			return base.VisitBinaryOperatorExpression(boe1);
		}
		#endregion
	}
}
