﻿using GiftyQueryLib.Config;
using GiftyQueryLib.Exceptions;
using GiftyQueryLib.Translators.Models;
using GiftyQueryLib.Utils;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GiftyQueryLib.Translators.SqlTranslators
{
    public class PostgreSqlTranslatorHelper
    {
        private readonly PostgreSqlConfig config;
        private readonly Dictionary<string, string> aliases;

        public PostgreSqlTranslatorHelper(PostgreSqlConfig config)
        {
            this.config = config;
            this.aliases = new();
        }

        /// <summary>
        /// PostgreSQL Auto-generated Aliases
        /// </summary>
        public (string key, string value)? AutoGeneratedAlias { get; set; } = null;

        #region Utils

        /// <summary>
        /// Get all property data of type
        /// </summary>
        /// <typeparam name="TItem">Type of entity</typeparam>
        /// <param name="exceptSelector">Data that should not be included into selection</param>
        /// <param name="extraType">Extra type</param>
        public virtual string ParsePropertiesOfType<TItem>(Expression<Func<TItem, object>>? exceptSelector = null, Type? extraType = null)
        {
            var exceptMembers = new List<MemberInfo>();
            var sb = new StringBuilder();

            if (exceptSelector is not null)
            {
                var body = exceptSelector?.Body;
                if (body is not null && body is NewExpression newExpression && newExpression.Type.Name.Contains("Anonymous"))
                {
                    foreach (var exp in newExpression.Arguments)
                    {
                        if (exp is MemberExpression memberExp)
                            exceptMembers.Add(memberExp.Member);
                    }
                }
            }

            var type = extraType ?? typeof(TItem);
            foreach (var property in type.GetProperties())
            {
                var attrData = GetAttrData(property);

                if (attrData.Value(AttrType.NotMapped) is not null || (exceptSelector is not null && exceptMembers.Any(it => it.Name == property.Name)))
                    continue;

                var fkAttr = attrData.Value(AttrType.ForeignKey);
                if (fkAttr is not null && property.IsCollection())
                    continue;

                if (fkAttr is null && property.IsCollection() &&
                    !Constants.StringTypes.Contains(property.GetGenericArg() ?? typeof(object)) &&
                    !Constants.NumericTypes.Contains(property.GetGenericArg() ?? typeof(object)))
                    continue;

                sb.Append(fkAttr is not null
                    ? string.Format(config.ColumnAccessFormat + ", ", config.Scheme, type.ToCaseFormat(config.CaseConfig), fkAttr.ConstructorArguments[0]!.Value!.ToString()!.ToCaseFormat(config.CaseConfig))
                    : string.Format(config.ColumnAccessFormat + ", ", config.Scheme, type.ToCaseFormat(config.CaseConfig), property.Name.ToCaseFormat(config.CaseConfig)));
            }

            var parsed = sb.ToString();
            return parsed;
        }

        /// <summary>
        /// Returns attribute data for property
        /// </summary>
        /// <param name="property">Target property</param>
        /// <returns></returns>
        public virtual Dictionary<AttrType, CustomAttributeData> GetAttrData(PropertyInfo? property)
        {
            var attributeData = property is null ? new List<CustomAttributeData>() : property.GetCustomAttributesData();
            var dict = new Dictionary<AttrType, CustomAttributeData>();

            foreach (var attr in attributeData)
            {
                if (config.KeyAttributes.Any(type => attr.AttributeType == type))
                    dict.Add(AttrType.Key, attr);
                else if (config.NotMappedAttributes.Any(type => attr.AttributeType == type))
                    dict.Add(AttrType.NotMapped, attr);
                else if (config.ForeignKeyAttributes.Any(type => attr.AttributeType == type))
                    dict.Add(AttrType.ForeignKey, attr);
                else if (config.JsonAttributes.Any(type => attr.AttributeType == type))
                    dict.Add(AttrType.Json, attr);
                else if (config.UseNamesProvidedInTableAttribute && attr.AttributeType.Name == AttrType.Table.ToString())
                    dict.Add(AttrType.Table, attr);
                else if (config.UseNamesProvidedInColumnAttribute && attr.AttributeType.Name == AttrType.Column.ToString())
                    dict.Add(AttrType.Column, attr);
            }

            return dict;
        }

        /// <summary>
        /// Returns properties and their values from entity
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity">Entity to parse</param>
        /// <param name="removeKeyProp">Determine if remove key prop or not</param>
        /// <returns></returns>
        /// <exception cref="BuilderException"></exception>
        public virtual Dictionary<string, object?> GetPopertyWithValueOfEntity<T>(T entity, bool removeKeyProp = true) where T : class
        {
            if (entity is null)
                throw new BuilderException("Entity cannot be null");

            var dict = new Dictionary<string, object?>();
            var props = entity.GetType().GetProperties();

            foreach (var property in props)
            {
                var attrData = GetAttrData(property);

                if ((attrData.Value(AttrType.Key) is not null && removeKeyProp) || attrData.Value(AttrType.NotMapped) is not null)
                    continue;

                string? propName = string.Empty;
                object? value = null;

                var fKAttr = attrData.Value(AttrType.ForeignKey);

                if (fKAttr is not null)
                {
                    if (!property.IsCollection())
                    {
                        propName = fKAttr.ConstructorArguments[0].Value?.ToString()?.ToCaseFormat(config.CaseConfig);

                        var keyProp = property.PropertyType.GetProperties().FirstOrDefault(prop => prop.GetCustomAttributes(true)
                              .FirstOrDefault(attr => config.KeyAttributes.Any(it => attr.GetType() == it)) is not null);
                        if (keyProp is null)
                            throw new BuilderException($"Related table class {property.Name} does not contain key attribute");

                        var keyPropValue = property.GetValue(entity);
                        value = keyProp.GetValue(keyPropValue);
                    }
                    else
                    {
                        var genericArg = property.GetGenericArg();
                        if (Constants.StringTypes.Contains(genericArg!) || Constants.NumericTypes.Contains(genericArg!))
                            throw new BuilderException("Primitive-typed collection should not be marked with foreign key attributes");
                        else
                        {
                            // Handle many to many
                            continue;
                        }
                    }
                }
                else
                {
                    propName = property.Name.ToCaseFormat(config.CaseConfig);
                    var val = property.GetValue(entity);

                    if (attrData.Value(AttrType.Json) is not null)
                    {
                        try
                        {
                            // TODO: Add possibility to change Json Serializator from default to custom (ex. Newtonsoft)
                            value = "{\"" + propName + "\": " + JsonSerializer.Serialize(val) + "}";
                        }
                        catch (Exception ex)
                        {
                            throw new BuilderException($"Cannot serialize \"{propName}\" json field. Details: {ex.Message}");
                        }
                    }
                    else
                    {
                        if (!property.IsCollection())
                            value = val;
                        else
                        {
                            var genericArg = property.GetGenericArg();

                            if (genericArg is null)
                                throw new BuilderException("Non-generic collections are not supported");

                            var isNumeric = Constants.NumericTypes.Contains(genericArg);
                            var isString = !isNumeric && Constants.StringTypes.Contains(genericArg);

                            if (isString || isNumeric)
                            {
                                if (val is null)
                                    value = "NULL";
                                else
                                {
                                    var collection = (IEnumerable)val;
                                    var collectionSb = new StringBuilder();

                                    foreach (var item in collection)
                                    {
                                        var defaultValue = item is null ? "NULL" : item.ToString();
                                        collectionSb.Append(defaultValue + ", ");
                                    }

                                    value = "{" + collectionSb.TrimEndComma() + "}";
                                }
                            }
                        }
                    }
                }

                dict.Add(propName ?? "", value);
            }

            return dict;
        }

        /// <summary>
        /// Gets Member's Atribute Arguments <br/>
        /// <b>Default:</b> Foreign Key Attributes as HashSet
        /// </summary>
        /// <param name="memberInfo">Member Info</param>
        /// <param name="attributeTypes">Attribute Types to retrieve arguments of them</param>
        /// <returns></returns>
        public virtual IList<CustomAttributeTypedArgument>? GetMemberAttributeArguments(MemberInfo? memberInfo, HashSet<Type> attributes, HashSet<Type>? attributeTypes = null)
        {
            if (memberInfo is null)
                return null;

            var foreignKeyAttr = memberInfo.CustomAttributes
                        .FirstOrDefault(attr => (attributeTypes is null ? attributes : attributeTypes).Any(type => attr.AttributeType == type));

            if (foreignKeyAttr is null)
                return null;

            return foreignKeyAttr.ConstructorArguments;
        }

        /// <summary>
        /// Checks if method exists
        /// </summary>
        /// <param name="methodName"></param>
        /// <returns></returns>
        /// <exception cref="BuilderException"></exception>
        public virtual bool CheckIfMethodExists(string? methodName) =>
            methodName is not null && Functions.ContainsKey(methodName)
            ? true
            : throw new BuilderException($"Funtion '{methodName}' is not registered into dictionary {nameof(Functions)}");

        /// <summary>
        /// Parse Property Selector into simplified member data item
        /// </summary>
        /// <param name="propertySelector">Property Row Selector</param>
        /// <returns>Member Data</returns>
        /// <exception cref="ArgumentException"></exception>
        public virtual MemberData GetMemberData<TItem>(Expression<Func<TItem, object>> propertySelector)
        {
            var body = propertySelector?.Body;

            if (body is null)
                throw new BuilderException("The row selector is null");

            if (body is MemberExpression memberExpression)
            {
                return new MemberData
                {
                    MemberType = body.Type,
                    CallerType = memberExpression.Expression!.Type,
                    MemberInfo = memberExpression.Member
                };
            }
            else if (body is UnaryExpression unaryExpression)
            {
                MemberInfo? memberInfo;

                if (unaryExpression.Operand is MethodCallExpression mcExp)
                    memberInfo = mcExp.Method;
                else if (unaryExpression.Operand is MemberExpression mExp)
                    memberInfo = mExp.Member;
                else
                    throw new BuilderException("Invalid operand expression");

                return new MemberData
                {
                    MemberType = unaryExpression.Type,
                    CallerType = unaryExpression.Operand is MethodCallExpression ? null : ((MemberExpression)unaryExpression.Operand)?.Expression?.Type,
                    MemberInfo = memberInfo
                };
            }
            else
                throw new BuilderException($"Invalid expression was provided");
        }

        #endregion

        #region Selector parser logic

        /// <summary>
        /// Parse Member Expression into string
        /// </summary>
        /// <param name="memberExp"></param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        public virtual string ParseMemberExpression(MemberExpression memberExp, string? paramName = null)
        {
            if (!config.NotMappedAttributes.Any(attr => memberExp.Member.GetCustomAttribute(attr) is not null))
            {
                var memberAttributes = GetMemberAttributeArguments(memberExp.Member, config.ForeignKeyAttributes);
                var memberName = memberAttributes is null
                    ? memberExp.Member.Name.ToCaseFormat(config.CaseConfig)
                    : memberAttributes.FirstOrDefault().ToString().Replace("\"", "");

                var result = string.Format(config.ColumnAccessFormat, config.Scheme, memberExp.Expression?.Type.ToCaseFormat(config.CaseConfig), memberName);

                if (paramName is null || memberExp.Member.Name.ToCaseFormat(config.CaseConfig) == paramName)
                    return result + ", ";
                else
                {
                    if (!aliases.TryAdd(paramName, result))
                        throw new BuilderException($"Alias \"{paramName}\" already exists");

                    return string.Format(result + " AS {0}, ", paramName);
                }
            }

            return string.Empty;
        }


        /// <summary>
        /// Parse Method Call Expression into string
        /// </summary>
        /// <param name="callExp"></param>
        /// <param name="baseType"></param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        /// <exception cref="BuilderException"></exception>
        public virtual string ParseMethodCallExpression(MethodCallExpression callExp, Type baseType, string? paramName = null, bool isSelectorParsing = false)
        {
            var sb = new StringBuilder();

            var methodName = callExp.Method.Name;
            var arguments = callExp.Arguments;

            string? translatedInnerExpression = null;

            Type? type = null;
            MemberInfo? memberInfo = null;

            if (arguments.Count == 0 && methodName == "Alias")
                return AutoGeneratedAlias?.value is null ? string.Empty : AutoGeneratedAlias.Value.value;

            if (arguments.Count == 2)
            {
                if (arguments[1] is MemberExpression mExp)
                {
                    type = mExp.Expression?.Type;
                    memberInfo = mExp.Member;
                }
                else if (arguments[1] is BinaryExpression bExp)
                {
                    var translator = new PostgreSqlTranslator(config);
                    translatedInnerExpression = translator.Translate(baseType, bExp);
                }
                else
                    throw new BuilderException($"The parameter of function/method is invalid in anonymous expression");
            }

            if (arguments[0] is UnaryExpression uArg)
            {
                if (uArg is not null && uArg.Operand is MemberExpression operand)
                {
                    type = operand.Expression?.Type;
                    memberInfo = operand.Member;
                }
                else if (uArg is not null && uArg.Operand is BinaryExpression bExp)
                {
                    var translator = new PostgreSqlTranslator(config);
                    translatedInnerExpression = translator.Translate(baseType, bExp);
                }
                else if (uArg is not null && uArg.Operand is LambdaExpression lExp)
                {
                    if (methodName == "Exists")
                    {
                        var translator = new PostgreSqlTranslator(config);
                        translatedInnerExpression = translator.Translate(baseType, lExp);
                    }
                }
                else
                    throw new BuilderException($"The parameter of function/method is invalid in anonymous expression");

            }
            else if (arguments[0] is MemberExpression mArg)
            {
                type = mArg.Expression?.Type;
                memberInfo = mArg.Member;
            }
            else if (arguments[0] is ParameterExpression pArg)
            {
                type = pArg.Type;
            }
            else if (arguments[0] is NewArrayExpression nExp)
            {
                if (CheckIfMethodExists(methodName))
                {
                    if (methodName == "Concat")
                    {
                        if (nExp.Expressions.Count < 2)
                            throw new BuilderException($"Cannot use CONCAT function with 1 or less number of arguments");

                        var columns = nExp.Expressions.Select(it =>
                        {
                            if (it is MemberExpression mExp)
                            {
                                var fkArgument = GetMemberAttributeArguments(mExp?.Member, config.ForeignKeyAttributes)?.FirstOrDefault();

                                var memeberName = fkArgument?.Value is not null ? fkArgument.Value.ToString() : mExp?.Member.Name.ToCaseFormat(config.CaseConfig);
                                return string.Format(config.ColumnAccessFormat, config.Scheme, mExp?.Expression?.Type.ToCaseFormat(config.CaseConfig), memeberName);
                            }
                            else if (it is UnaryExpression uExp && uExp is not null)
                            {
                                var operand = uExp.Operand;
                                var translator = new PostgreSqlTranslator(config);
                                return translator.Translate(baseType, operand);
                            }
                            else if (it is MethodCallExpression mcExp && mcExp is not null)
                            {
                                var translator = new PostgreSqlTranslator(config);
                                return translator.Translate(baseType, mcExp);
                            }
                            else
                                throw new BuilderException($"The parameter of function/method is invalid in anonymous expression");

                        });

                        var result = string.Format(Functions[methodName], string.Join(',', columns));

                        if (paramName is null)
                            return result + ", ";

                        if (!aliases.TryAdd(paramName, result))
                            throw new BuilderException($"Alias \"{paramName}\" already exists");

                        return result + (paramName is null ? "" : " AS " + paramName) + ", ";
                    }
                }
            }
            else if (arguments[0] is ConstantExpression cEx)
            {
                if (methodName == "Alias")
                {
                    var alias = cEx?.Value?.ToString()?.ToCaseFormat(config.CaseConfig);
                    if (alias is null)
                        throw new BuilderException($"Alias cannot be null or empty while using HAVING statement");

                    var aliasExists = aliases.TryGetValue(alias, out string? value);

                    if (!aliasExists)
                        throw new BuilderException($"Alias \"{cEx?.Value}\" does not exist");

                    if (value is null && string.IsNullOrEmpty(value))
                        throw new BuilderException($"Alias \"{cEx?.Value}\" cannot have an empty value");

                    return isSelectorParsing ? alias : value;
                }
            }
            else
                throw new BuilderException($"Unsupported expression in provided arguments");


            var memberAttributes = GetMemberAttributeArguments(memberInfo, config.ForeignKeyAttributes);

            if (translatedInnerExpression is null)
            {
                if (memberInfo is null)
                    throw new BuilderException($"Invalid method call on provided expression");

                string format = config.ColumnAccessFormat;

                if (CheckIfMethodExists(methodName))
                {
                    // TODO: Check and provide allowed types for function
                    format = string.Format(Functions[methodName], format);
                }

                var memberName = memberAttributes is null
                    ? memberInfo.Name.ToCaseFormat(config.CaseConfig)
                    : memberAttributes.FirstOrDefault().ToString();

                var result = string.Format(format, config.Scheme, type?.ToCaseFormat(config.CaseConfig), memberName);

                if (methodName == "Distinct")
                    result += string.Format(" " + config.ColumnAccessFormat, config.Scheme, type?.ToCaseFormat(config.CaseConfig), memberName);

                if (paramName is null)
                    sb.Append(result + ", ");
                else
                {
                    if (!aliases.TryAdd(paramName, result))
                        throw new BuilderException($"Alias \"{paramName}\" already exists");

                    sb.Append(result + $" AS {paramName}, ");
                }
            }
            else
            {
                if (CheckIfMethodExists(methodName) && methodName != "Distinct")
                {
                    string result = string.Format(Functions[methodName], translatedInnerExpression);

                    if (paramName is null)
                        sb.Append(result + ", ");
                    else
                    {
                        if (!aliases.TryAdd(paramName, result))
                            throw new BuilderException($"Alias \"{paramName}\" already exists");
                        sb.Append(result + $" AS {paramName}, ");
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parse Binary Expression into string
        /// </summary>
        /// <param name="bExp"></param>
        /// <param name="baseType"></param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        public virtual string ParseBinaryExpression(BinaryExpression bExp, Type baseType, string? paramName = null)
        {
            if (Constants.StringTypes.Contains(bExp.Type))
                throw new BuilderException($"Binary expression cannot be parsed when left or right operands have type {bExp.Type}. If you want concat strings use PConcat function instead.");

            var translator = new PostgreSqlTranslator(config);
            string translatedBinary = translator.Translate(baseType, bExp);

            if (paramName is null)
                return translatedBinary + ", ";
            else
            {
                if (!aliases.TryAdd(paramName, translatedBinary))
                    throw new BuilderException($"Alias \"{paramName}\" already exists");
                return string.Format("{0} AS {1}, ", translatedBinary, paramName);
            }
        }

        #endregion

        #region Visitor method call parser logic

        /// <summary>
        /// Gets translated C# method into PostgreSQL function
        /// </summary>
        /// <param name="m"></param>
        /// <param name="baseType"></param>
        /// <returns></returns>
        public virtual string GetMethodTranslated(MethodCallExpression m, Type baseType)
        {
            return m.Method.Name switch
            {
                "Contains" => GetContainsMethodTranslated(m, baseType),
                "ToLower" or "ToLowerInvariant" => GetUpperLowerMethodTranslated(m, baseType, true),
                "ToUpper" or "ToUpperInvariant" => GetUpperLowerMethodTranslated(m, baseType, false),
                "ToString" => GetToStringMethodTranslated(m, baseType),
                "Any" => GetAnyMethodTranslated(m),
                _ => string.Empty,
            };
        }

        /// <summary>
        /// Contains method translator
        /// </summary>
        /// <param name="m"></param>
        /// <param name="baseType"></param>
        /// <returns></returns>
        /// <exception cref="BuilderException"></exception>
        protected virtual string GetContainsMethodTranslated(MethodCallExpression m, Type baseType)
        {
            var sb = new StringBuilder();
            var arg = string.Empty;
            var argNullable = false;

            Expression? expObj = null;

            if (m.Arguments.Count == 1)
            {
                if (m.Arguments[0] is ConstantExpression cArg)
                {
                    if (m.Object is MethodCallExpression mcExp && mcExp.Method.Name == "Alias")
                    {
                        sb.AppendFormat(" {0} LIKE '%{1}%' ", ParseMethodCallExpression(mcExp, baseType, isSelectorParsing: true), cArg.Value);
                    }
                    else if (m.Object is MemberExpression mExp)
                    {
                        sb.AppendFormat(" " + config.ColumnAccessFormat + " LIKE '%{3}%' ",
                            config.Scheme,
                            mExp!.Expression!.Type.ToCaseFormat(config.CaseConfig),
                            mExp.Member.Name.ToCaseFormat(config.CaseConfig),
                            cArg.Value);
                    }
                    else
                        throw new BuilderException("Contains Method argument is invalid");

                    return sb.ToString();
                }
                else if (m.Arguments[0] is MemberExpression mArg)
                {
                    if (mArg?.Member is null)
                        throw new BuilderException("Member expression should not be null");

                    arg = mArg.Member.Name;
                    argNullable = Nullable.GetUnderlyingType((mArg.Member as PropertyInfo)!.PropertyType) != null;
                    expObj = m.Object;
                }
                else if (m.Arguments[0] is UnaryExpression uArg && uArg.Operand is MemberExpression umExp)
                {
                    if (umExp?.Member is null)
                        throw new BuilderException("Member expression should not be null");

                    arg = umExp.Member.Name;
                    argNullable = Nullable.GetUnderlyingType((umExp.Member as PropertyInfo)!.PropertyType) != null;
                    expObj = m.Object;
                }
                else
                    throw new BuilderException("Contains Method argument is invalid");

            }
            else if (m.Arguments.Count == 2)
            {
                arg = ((MemberExpression)m.Arguments[1]).Member.Name;
                expObj = m.Arguments[0];
            }

            if (expObj is null)
                throw new BuilderException("Object should not be null");

            bool isEnumerable = typeof(IEnumerable).IsAssignableFrom(expObj.Type);
            bool isArray = typeof(Array).IsAssignableFrom(expObj.Type);

            if (expObj is NewArrayExpression or ListInitExpression)
            {
                void localAction(Expression exp, List<object> items)
                {
                    if (exp is ConstantExpression cExp)
                        items.Add(ConvertToItemWithType(cExp.Value));
                    else if (exp is MethodCallExpression mExp)
                        items.Add(GetMethodTranslated(mExp, baseType));
                    else
                        throw new BuilderException("Invalid data");
                }

                var items = new List<object>();

                if (expObj is NewArrayExpression arrExp)
                {
                    var exps = arrExp.Expressions;

                    foreach (var e in exps)
                        localAction(e, items);

                }
                else if (expObj is ListInitExpression listExp)
                {
                    var inits = listExp.Initializers;

                    if (!inits.Any())
                        items.Add(string.Empty);

                    foreach (var init in inits)
                        localAction(init.Arguments[0], items);
                }

                sb.Append(string.Format(" " + config.ColumnAccessFormat + " IN ({3}) ", config.Scheme, baseType.ToCaseFormat(config.CaseConfig), arg.ToCaseFormat(config.CaseConfig), string.Join(',', items)));
                return sb.ToString();
            }

            var obj = (expObj as ConstantExpression)!;

            if ((obj.Type.IsGenericType && obj.Type.GenericTypeArguments.Length == 1 && isEnumerable) || isArray)
            {
                var genericArg = isArray ? obj.Type : obj.Type.GenericTypeArguments[0];

                if (genericArg is null)
                    throw new BuilderException("Non-generic collections are not supported");

                var val = obj.Value;

                if (val is null)
                    throw new BuilderException("Object value should not be null");

                var collection = (IEnumerable)val;
                var collectionSb = new StringBuilder();
                var isNumeric = Constants.NumericTypes.Contains(genericArg);
                var isString = !isNumeric && Constants.StringTypes.Contains(genericArg);

                if (!isNumeric && !isString)
                    throw new BuilderException($"Invalid type {genericArg.Name} of collection");

                foreach (var item in collection)
                {
                    // TODO: Create a map with default values for types
                    var value = item is null ? (argNullable ? "NULL" : (isNumeric ? "0" : "''")) : (isString ? $"'{item}'" : item.ToString());
                    collectionSb.Append(value + ", ");
                }

                string result = string.Format(" " + config.ColumnAccessFormat + " IN ({3})",
                    config.Scheme, baseType.ToCaseFormat(config.CaseConfig),
                    arg.ToCaseFormat(config.CaseConfig), collectionSb.TrimEndComma());

                return result;
            }
            else
                throw new BuilderException($"Unsupported type {obj.Type}");
        }

        /// <summary>
        /// Upper-Lower method translator
        /// </summary>
        /// <param name="m"></param>
        /// <param name="isLower"></param>
        /// <returns></returns>
        /// <exception cref="BuilderException"></exception>
        protected virtual string GetUpperLowerMethodTranslated(MethodCallExpression m, Type baseType, bool isLower)
        {
            if (m.Object is null)
                throw new BuilderException("Object should not be null");

            if (m.Object is MemberExpression memberExp)
            {
                return string.Format(" " + (isLower ? "LOWER" : "UPPER") + "(" + config.ColumnAccessFormat + ")", config.Scheme, baseType.ToCaseFormat(config.CaseConfig), memberExp.Member.Name.ToCaseFormat(config.CaseConfig));
            }
            else if (m.Object is ConstantExpression constExp)
            {
                string? value = constExp.Value?.ToString();
                value = isLower ? value?.ToLower() : value?.ToUpper();
                return string.Format("'{0}' ", value);
            }

            return string.Empty;
        }

        /// <summary>
        /// ToString method translator
        /// </summary>
        /// <param name="m"></param>
        /// <param name="baseType"></param>
        /// <returns></returns>
        /// <exception cref="BuilderException"></exception>
        protected virtual string GetToStringMethodTranslated(MethodCallExpression m, Type baseType)
        {
            if (m.Object is null)
                throw new BuilderException("Object should not be null");

            if (m.Object is MemberExpression memberExp)
            {
                return string.Format(config.ColumnAccessFormat + "::text", config.Scheme, baseType.ToCaseFormat(config.CaseConfig), memberExp.Member.Name.ToCaseFormat(config.CaseConfig));
            }
            else if (m.Object is ConstantExpression constExp)
            {
                return string.Format("'{0}' ", constExp?.Value?.ToString());
            }

            return string.Empty;
        }

        /// <summary>
        /// Any method translator
        /// </summary>
        /// <param name="m"></param>
        /// <returns></returns>
        /// <exception cref="BuilderException"></exception>
        protected virtual string GetAnyMethodTranslated(MethodCallExpression m)
        {
            if (m?.Arguments is null || m.Arguments?.Count == 0)
                throw new BuilderException("Invalid caller object. It should be a generic collection with one generic parameter");

            if (m.Arguments is null || m.Arguments[0] is not MemberExpression mExp)
                throw new BuilderException("Invalid caller object. It should be a generic collection with one generic parameter");

            if (m.Arguments is not null && m.Arguments.Count > 2)
                throw new BuilderException("Invalid amount of arguments");

            bool isEnumerable = typeof(IEnumerable).IsAssignableFrom(mExp.Type);
            bool isArray = typeof(Array).IsAssignableFrom(mExp.Type);

            if (!((mExp.Type.IsGenericType && mExp.Type.GenericTypeArguments.Length == 1 && isEnumerable) || isArray))
                throw new BuilderException("Invalid caller object. It should be a generic collection with one generic parameter");

            var type = mExp.Type.GenericTypeArguments[0].ToCaseFormat(config.CaseConfig);

            if (m.Arguments is not null && m.Arguments.Count == 2)
            {
                if (m.Arguments[1] is not LambdaExpression lExp)
                    throw new BuilderException("Invalid arguments. It should be an expression");

                var parsedExpression = new PostgreSqlTranslator(config).Translate(mExp.Type.GenericTypeArguments[0], lExp);
                return string.Format(" EXISTS (SELECT 1 FROM {0}.{1} WHERE {2} LIMIT 1) ", config.Scheme, type, parsedExpression);
            }

            return string.Format(" EXISTS (SELECT 1 FROM {0}.{1} LIMIT 1) ", config.Scheme, type);
        }

        protected virtual string ConvertToItemWithType(object? item)
        {
            if (item is null)
                return string.Empty;

            if (Constants.StringTypes.Contains(item.GetType()))
                return string.Format("'{0}'", item.ToString());
            else
                return string.Format("{0}", item.ToString());
        }

        #endregion

        #region Predefined Config

        /// <summary>
        /// PostgreSQL Expression Types
        /// </summary>
        public Dictionary<ExpressionType, string[]> ExpressionTypes => new()
        {
            { ExpressionType.Convert, new [] { string.Empty } },
            { ExpressionType.Not, new [] { " NOT " } },
            { ExpressionType.And, new [] { " AND " } },
            { ExpressionType.AndAlso, new [] { " AND " } },
            { ExpressionType.Or, new [] { " OR " } },
            { ExpressionType.OrElse, new [] { " OR " } },
            { ExpressionType.Equal, new[] { " IS ", " = " } },
            { ExpressionType.NotEqual, new[] { " IS NOT ", " != " } },
            { ExpressionType.LessThan, new[] { " < " } },
            { ExpressionType.LessThanOrEqual, new[] { " <= " } },
            { ExpressionType.GreaterThan, new[] { " > " } },
            { ExpressionType.GreaterThanOrEqual, new[] { " >= " } },
            { ExpressionType.Add, new[] { " + " } },
            { ExpressionType.Subtract, new[] { " - " } },
            { ExpressionType.Multiply, new[] { " * " } },
            { ExpressionType.Divide, new[] { " / " } },
            { ExpressionType.Modulo, new[] { " % " } },
            { ExpressionType.ExclusiveOr, new[] { " ^ " } }
        };

        /// <summary>
        /// PostgreSQL Functions Syntaxes
        /// </summary>
        public Dictionary<string, string> Functions => new()
        {
            { "Count", "COUNT({0})" },
            { "Sum", "SUM({0})" },
            { "Avg", "AVG({0})" },
            { "Min", "MIN({0})"},
            { "Max", "MAX({0})" },
            { "Concat", "CONCAT({0})" },
            { "Alias", "" },
            { "Distinct", "DISTINCT ON ({0})" },
            { "Exists", "EXISTS ({0})" }
        };

        #endregion
    }
}