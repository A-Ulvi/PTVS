// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.


namespace Microsoft.PythonTools.Interpreter {
    /// <summary>
    /// Represents a built-in property which has a getter/setter.  
    /// </summary>
    public interface IBuiltinProperty : IMember {
        /// <summary>
        /// The type of the value the property gets/sets.
        /// </summary>
        IPythonType Type {
            get;
        }

        /// <summary>
        /// True if the property is static (declared on the class) not the instance.
        /// </summary>
        bool IsStatic {
            get;
        }

        /// <summary>
        /// Documentation for the property.
        /// </summary>
        string Documentation {
            get;
        }

        /// <summary>
        /// A user readable description of the property.
        /// </summary>
        string Description {
            get;
        }
    }
}
