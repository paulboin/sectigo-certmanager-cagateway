﻿// Copyright 2021 Keyfactor
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Keyfactor.AnyGateway.Sectigo.API
{
    public class ListOrganizationsResponse
    {
        public List<Organization> Organizations { get; set; }
    }

    public class Organization
    {
        public int id { get; set; }
        public string name { get; set; }
        public List<CertificateType> certTypes { get; set; }
        public List<Department> departments { get; set; }
    }

    public class Department
    {
        public int id { get; set; }
        public string name { get; set; }
        public List<CertificateType> certTypes { get; set; }
    }

}
