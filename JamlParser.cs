using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;

namespace FormatConverter.Parser
{
    //Parses a jaml file (for more information, read example.yml) into an object of specified type T
    class JamlParser<T> where T : new()
    {
        private int lineIndex = 0;

        /// <summary>
        /// Content to parse
        /// </summary>
        private string[] lines;
        /// <summary>
        /// Path to file (For logging)
        /// </summary>
        private string path = "";


        /// <summary>
        /// Contains the count of whitespaces for each layer and the object each layer refers to
        /// </summary>
        private Stack<Layer> layers = new Stack<Layer>();

        // Constants to clarify the output of checkLayer()
        /// <summary>
        /// Layer up means this line contains a higher indentation than the last. This means last lines object will now be filled with the following data
        /// </summary>
        private const int LAYER_UP = 1;
        /// <summary>
        /// Layer down means this line contains a lower indentation than the last. This means that the last object is now filled and returned to the parent object
        /// </summary>
        private const int LAYER_DOWN = -1;
        /// <summary>
        /// Layer same means this line contains the same indentation than the last. The parser will continue to fill the current object's fields
        /// </summary>
        private const int LAYER_SAME = 0;

        /// <summary>
        /// Parses Jaml file and maps output to object
        /// </summary>
        /// <param name="path">Path to file</param>
        /// <returns>Filled object</returns>
        public T parse(string path)
        {
            this.path = path;
            string[] lines = System.IO.File.ReadAllLines(path);
            return parse(lines);
        }

        /// <summary>
        /// Parses list of Jaml commands and maps output to object
        /// </summary>
        /// <param name="lines">Jaml commands</param>
        /// <returns>Filled object</returns>
        public T parse(List<string> lines)
        {
            return parse(lines.ToArray());
        }

        /// <summary>
        /// Parses array of Jaml commands and maps output to object
        /// </summary>
        /// <param name="lines">Jaml commands</param>
        /// <returns>Filled object</returns>
        public T parse(string[] lines)
        {
            this.lines = lines;
            //init base layer
            T obj = new T();
            layers.Push(new Layer(0, obj));

            return parseElement<T>(obj);
        }

        /// <summary>
        /// Takes object and passes it to parseObject, with the adjacent ElementParser for special syntax for lists and dictionaries
        /// </summary>
        /// <typeparam name="E">Type of the Element</typeparam>
        /// <param name="element">Instantiated object</param>
        /// <returns>Filled object</returns>
        private E parseElement<E>(E element)
        {
            //E inherits from IDictionary
            if (typeof(E).GetInterfaces().Any(x =>
                x.IsGenericType &&
                x.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
                return parseObject(element, new DictionaryParser<E>());

            //E inherits from IList
            else if (typeof(E).GetInterfaces().Any(x =>
                x.IsGenericType &&
                x.GetGenericTypeDefinition() == typeof(IList<>)))
                return parseObject(element, new ListParser<E>());

            //Else, E is any Object without any special Format in jaml
            else
                return parseObject(element, new ObjectParser<E>());
        }

        /// <summary>
        /// reads lines and passes discovered syntax to the elementparser
        /// </summary>
        /// <typeparam name="O">Type of the object</typeparam>
        /// <param name="obj">Instantiated object</param>
        /// <param name="parser">Parser to fill object with data</param>
        /// <returns>Filled object</returns>

        private O parseObject<O>(O obj, IElementParser<O> parser)
        {
            for (lineIndex += 0; lineIndex < lines.Length; lineIndex++)
            {
                //empty line or comment
                string line = lines[lineIndex];
                Regex ignore = new Regex(@"^\s*(#|\s*$)");
                if (ignore.Match(line).Success)
                    continue;

                //jaml statement
                Regex regex = new Regex("^(?<layer>\\s*)(?<key>\\S*)\\s*[:-]\\s*(?<value>\".*\"|.*?)\\s*(?:#|$)");
                Match match = regex.Match(line);
                if (!match.Success)
                    throw new JamlException("Could not parse \"" + line + "\"" + getLocationInfo() + ". Unexpected format");
                int layer = match.Groups["layer"].Value.Length;

                //finding out if last defined element has fields to be filled (LAYER_UP) or if this object is done (LAYER_DOWN)
                int layerDiff = checkLayer(layer);
                if (layerDiff == LAYER_DOWN)
                {
                    layers.Pop();
                    return obj;
                }
                if (layerDiff == LAYER_UP)
                {
                    dynamic nextObject = layers.Peek().LastElement;
                    layers.Push(new Layer(layer, null));
                    parseElement(nextObject);
                    // it might be that with the end of the parsed child object, the parent ends as well, decrementing index = checking same line against parent
                    lineIndex--;
                    continue;
                }

                // if jaml statement found in same layer, it's a field for the current object (layerObject of most recent layer), so pass it to the parser
                layers.Peek().LastElement = parser.acceptMatch(this, obj, match);
            }

            //eof -> return object
            return obj;
        }

        /// <summary>
        /// Compares whitespaces to the last entry on the stack to see if the range of an object has been entered or left
        /// </summary>
        /// <param name="size">amount of whitespaces</param>
        /// <returns>-1 if layer down, 1 if layer up, 0 if in same layer</returns>
        private int checkLayer(int size)
        {
            if (size < layers.Peek().Indentation)
            {
                return LAYER_DOWN;
            }
            if (size > layers.Peek().Indentation)
            {
                return LAYER_UP;
            }
            return LAYER_SAME;
        }

        /// <summary>
        /// Tries to parse the read string value into the necessary fieldtype of the object of type T
        /// Needs to be public so i can be found by reflection
        /// </summary>
        /// <typeparam name="V">Type of the expected field</typeparam>
        /// <param name="var">Current value of the field</param>
        /// <param name="value">New value of the field</param>
        /// <param name="type">Type of the field, as in some cases 'V' is passed as object{type}</param>
        /// <returns>value in correct type</returns>
        public V parseValue<V>(V var, string value, Type type)
        {
            var parsed = default(V);
            value = value.Trim();

            //if quoted, remove quotes
            if (value.StartsWith("\"") && value.EndsWith("\""))
            {
                value = value.Substring(1, value.Length - 2);
            }
            try
            {
                parsed = (V)Convert.ChangeType(value, type);
            }
            //Convert.ChangeType recognized the type, but can't parse it
            catch (System.FormatException)
            {
                throw new JamlException("\"" + value + "\" cannot be converted into " + type + getLocationInfo());
            }
            //Convert.ChangeType doesn't recognize the type, so it's not a primitive -> instantiate object
            catch (System.InvalidCastException)
            {
                parsed = instantiate<V>(value, type);
            }
            return parsed;
        }

        /// <summary>
        /// Tries to parse a custom constructor if given, or uses default if not
        /// </summary>
        /// <typeparam name="O">Type of the object</typeparam>
        /// <param name="value">Constructor</param>
        /// <param name="type">Type of the field, as in some cases 'O' is passed as object{type}</param>
        /// <returns>Instatiated object of O</returns>
        private O instantiate<O>(string value, Type type)
        {
            Type classType = type;
            //default
            if (value.Equals(""))
                return (O)Activator.CreateInstance(classType);

            //match "x.y.z(a,b,c)"
            Regex regex = new Regex("\"?(?<class>[\\w\\d\\._]*)?(?:<(?<generic>[\\w\\d\\._]*,?)>)?(?:\\((?<args>.*)\\))?\"?");
            Match match = regex.Match(value);
            if (!match.Success)
                throw new JamlException("Could not parse \"" + lines[lineIndex] + "\"" + getLocationInfo() + ". Constructor is malformed");

            string classPath = match.Groups["class"].Value;
            string[] args = match.Groups["args"].Value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            string[] genericTypes = match.Groups["generic"].Value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            //if generic values were found, make class generic
            if (genericTypes.Length != 0)
                classPath += "`" + genericTypes.Length + "[" + match.Groups["generic"].Value + "]";

            //if class is empty, use default (object itself)
            if (classPath != "")
                try
                {
                    classType = Type.GetType(classPath, true);
                }
                catch
                {
                    classType = type.Assembly.GetType(classPath, false);
                }
            if (classType == null)
                throw new JamlException("Class \"" + classPath + "\" could not be found" + getLocationInfo());

            //try to make it generic


            //when args are empty, use default (no arguments)
            if (args.Length == 0)
                return (O)Activator.CreateInstance(classType);

            //try to find a constructor that matches signature
            Object[] parsedArgs = new Object[args.Length];
            var constructors = classType.GetConstructors().Where(c => c.GetParameters().Length == args.Length);

            //invoke constructors and use first that the parser sucessfully could parse the arguments for
            ConstructorInfo constructor = null;
            foreach (ConstructorInfo ctor in constructors)
            {
                if (constructor == null)
                    for (int i = 0; i < args.Length; i++)
                    {
                        try
                        {
                            var parseMethod = this.GetType().GetMethod("parseValue").MakeGenericMethod(ctor.GetParameters()[i].ParameterType);
                            Object[] param = new Object[] { null, args[i], ctor.GetParameters()[i].ParameterType };
                            parsedArgs[i] = invokeMethod(parseMethod, this, param);
                            constructor = ctor;
                        }
                        catch
                        {
                            //if parser can't parse one of the arguments, skip constructor
                            constructor = null;
                            break;
                        }
                    }
            }
            if (constructor == null)
                throw new JamlException("No constructor found that matches \"" + value + "\"" + getLocationInfo());

            return (O)constructor.Invoke(parsedArgs);
        }

        /// <summary>
        /// Invokes method and disposes uninformative Exception thrown by Reflection, and instead throws InnerException (the one actually being thrown)
        /// </summary>
        /// <param name="method">Method to be invoked</param>
        /// <param name="obj">Object to be invoked on</param>
        /// <param name="param">Parameters of method</param>
        /// <returns>Return of the method</returns>
        private Object invokeMethod(MethodInfo method, Object obj, Object[] param)
        {
            try
            {
                return method.Invoke(obj, param);
            }
            catch (Exception e)
            {
                throw e.InnerException;
            }
        }

        /// <summary>
        /// Nicely formats info for exceptions
        /// </summary>
        /// <returns>File and Line where exception occured</returns>
        private string getLocationInfo()
        {
            return " (" + (path.Equals("") ? "" : path + ", ") + "line " + (lineIndex + 1) + ") ";
        }



        /// <summary>
        /// Lists and Maps have a different yaml syntax than plain objects. Thats why there are 3 elementParsers that derive from this interface.
        /// One for each type: Object, List, Dictionary. Each with the sole purpose of parsing a match into a field for the element.
        /// </summary>
        /// <typeparam name="Tobj"></typeparam>
        private interface IElementParser<Tobj>
        {
            /// <summary>
            /// Receives the data that was matched to process it, and return whatever object was instantiated (incase said object gets fields assigned in the next line)
            /// </summary>
            /// <typeparam name="X">Type of object the JamlParser parses into. Not needed in code, just for the parameter parser</typeparam>
            /// <param name="parser">The parser</param>
            /// <param name="obj">The object the parser is currently filling</param>
            /// <param name="match">The data that was matched</param>
            /// <returns>The instantiated object</returns>
            Object acceptMatch<X>(JamlParser<X> parser, Tobj obj, Match match) where X : new();
        }

        private class ObjectParser<Tobj> : IElementParser<Tobj>
        {
            public Object acceptMatch<X>(JamlParser<X> parser, Tobj obj, Match match) where X : new()
            {
                FieldInfo field = typeof(Tobj).GetField(match.Groups["key"].Value);
                if (field == null)
                    throw new JamlException(match.Groups["key"].Value + parser.getLocationInfo() + "is no attribute of " + typeof(Tobj).ToString());

                Object value = parser.parseValue(field.GetValue(obj), match.Groups["value"].Value, field.FieldType);
                field.SetValue(obj, value);
                return value;
            }
        }

        private class DictionaryParser<Tobj> : IElementParser<Tobj>
        {
            private Type key = typeof(Tobj).GetGenericArguments()[0];
            private Type value = typeof(Tobj).GetGenericArguments()[1];

            public Object acceptMatch<X>(JamlParser<X> parser, Tobj obj, Match match) where X : new()
            {
                var valueParseMethod = parser.GetType().GetMethod("parseValue").MakeGenericMethod(value);
                Object[] valueParam = new Object[] { null, match.Groups["value"].Value, value };
                dynamic parsedValue = parser.invokeMethod(valueParseMethod, parser, valueParam);

                var keyParseMethod = parser.GetType().GetMethod("parseValue").MakeGenericMethod(key);
                Object[] keyParam = new Object[] { null, match.Groups["key"].Value, key };
                dynamic parsedKey = parser.invokeMethod(keyParseMethod, parser, keyParam);

                obj.GetType().GetMethod("Add").Invoke(obj, new[] { parsedKey, parsedValue });
                return parsedValue;
            }
        }

        private class ListParser<Tobj> : IElementParser<Tobj>
        {
            private Type value = typeof(Tobj).GetGenericArguments()[0];

            public Object acceptMatch<X>(JamlParser<X> parser, Tobj obj, Match match) where X : new()
            {
                var parseMethod = parser.GetType().GetMethod("parseValue").MakeGenericMethod(value);
                Object[] param = new Object[] { null, match.Groups["value"].Value, value };
                dynamic result = parser.invokeMethod(parseMethod, parser, param);
                obj.GetType().GetMethod("Add").Invoke(obj, new[] { result });
                return result;
            }
        }

        /// <summary>
        /// Describes a (vertical) layer of the config. Saves the last known state of position and value to compare with the next line.
        /// </summary>
        public class Layer
        {
            public int Indentation;
            public Object LastElement;

            public Layer() { }

            public Layer(int indentation, Object lastElement)
            {
                Indentation = indentation;
                LastElement = lastElement;
            }
        }

        /// <summary>
        /// Exception to be thrown, when the Parser runs into unexpected values/behavior
        /// </summary>
        [Serializable]
        private class JamlException : Exception
        {
            public JamlException()
            { }

            public JamlException(string message)
                : base(message)
            { }

            public JamlException(string message, Exception innerException)
                : base(message, innerException)
            { }
        }
    }
}
